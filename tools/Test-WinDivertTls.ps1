# Run in elevated PowerShell with a trusted WinDivert 2.2 x64 DLL and driver already loaded.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DagamiExe,
    [Parameter(Mandatory)][string]$WinDivertDll,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$WinDivertSha256,
    [Parameter(Mandatory)][string]$IPv4,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$TlsHost,
    [ValidateRange(1,65535)][int]$Port = 443,
    [ValidateRange(1024,65535)][int]$SourcePort = 55043
)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test from elevated PowerShell.'
}
$address = [Net.IPAddress]::Parse($IPv4)
if ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $address.ToString() -ne $IPv4) {
    throw 'Use a canonical IPv4 address.'
}
$exe = (Resolve-Path -LiteralPath $DagamiExe).Path
$dll = (Resolve-Path -LiteralPath $WinDivertDll).Path
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $WinDivertSha256) {
    throw 'WinDivert DLL hash mismatch.'
}

function Invoke-TlsCheck([int]$LocalPort) {
    $arguments = @('--ipv4','--noproxy','*','--http1.1','--connect-timeout','4','--max-time','7',
        '--silent','--show-error','--output','NUL','--write-out','%{http_code}',
        '--resolve',"${TlsHost}:${Port}:${IPv4}","https://${TlsHost}:${Port}/")
    if ($LocalPort) { $arguments += @('--local-port',"$LocalPort") }
    $code = & curl.exe @arguments
    if ($LASTEXITCODE -ne 0) { return $null }
    return $code
}

$baseline = Invoke-TlsCheck 0
if (!$baseline) { throw 'Baseline HTTPS failed. Choose a reachable TLS endpoint for the comparison.' }
"Baseline HTTPS: HTTP $baseline"

$process = [Diagnostics.Process]::new()
$process.StartInfo = [Diagnostics.ProcessStartInfo]::new($exe)
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.Arguments = '--packet-test "{0}" {1} {2} {3} {4} {5}' -f $dll,$WinDivertSha256,$IPv4,$SourcePort,$Port,$TlsHost
try {
    if (!$process.Start()) { throw 'Unable to launch Dagami.' }
    $errors = $process.StandardError.ReadToEndAsync()
    $ready = $process.StandardOutput.ReadLineAsync()
    if (!$ready.Wait(5000)) { throw 'WinDivert did not signal readiness.' }
    if ($ready.Result -notlike 'Capture active*') {
        if (!$process.WaitForExit(2000)) { throw 'WinDivert did not signal readiness.' }
        throw ('WinDivert did not start: ' + $errors.GetAwaiter().GetResult())
    }
    $duringCapture = Invoke-TlsCheck $SourcePort
    $output = $process.StandardOutput.ReadToEndAsync()
    if (!$process.WaitForExit(22000)) { throw 'WinDivert test did not exit.' }
    $report = $output.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or !$duringCapture) {
        throw ("Packet test failed (exit {0}, HTTP {1}). {2} {3}" -f
            $process.ExitCode,$duringCapture,$report,$errors.GetAwaiter().GetResult())
    }
    "WinDivert HTTPS: HTTP $duringCapture"
    $report.Trim()
} finally {
    try { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } }
    catch [InvalidOperationException] {}
    $process.Dispose()
}
