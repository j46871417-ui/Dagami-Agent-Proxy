$ErrorActionPreference='Stop'
$engine=Join-Path $PSScriptRoot 'SystemDns.ps1'
if(-not (Test-Path -LiteralPath $engine)){$engine=Join-Path $PSScriptRoot '..\..\SystemDns.ps1'}
. $engine -LibraryOnly
$script:Checks=0
function Assert($Value,[string]$Message){$script:Checks++;if(-not $Value){throw $Message}}
function Reject([scriptblock]$Action,[string]$Message){$caught=$false;try{& $Action | Out-Null}catch{$caught=$true};Assert $caught $Message}
Assert (@(Get-ProviderDomains Full).Count -eq 76) 'Script domain list mismatch'
Assert (@(Get-ProviderDomains Development).Count -lt 76) 'Development preset not filtered'
foreach($domain in @('accounts.google.com','oauth2.googleapis.com','lh3.googleusercontent.com','www.gstatic.com','ssl.gstatic.com')) {
    Assert ($domain -in @(Get-ProviderDomains Development)) ('Missing Google dependency: '+$domain)
}
$script:BaseRoot=Join-Path $env:TEMP ('DevProxy-systemdns-tests-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:BaseRoot) | Out-Null

# Every network mutator and registry/adapter reader below is a fixture.
# The real Windows DNS cmdlets are never invoked in these tests.
function Assert-Administrator {}
function Protect-Path {param($Path,$Directory)}
function Get-StoreRoot {$script:TestRoot}
function Read-State { $path=Join-Path (Get-StoreRoot) 'state.json';if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path -Raw | ConvertFrom-Json} }
function Get-ProviderDomains {param($Preset) @('aistudio.google.com','cloudcode-pa.googleapis.com','daily-cloudcode-pa.googleapis.com')}
function Get-NetAdapter {param([switch]$IncludeHidden) $script:Adapters}
function Get-ItemProperty {param($LiteralPath,$Name,$ErrorAction)
    $id=[regex]::Match($LiteralPath,'\{([0-9a-fA-F-]+)\}$').Groups[1].Value
    $family=if($LiteralPath -match '\\Tcpip6\\'){'IPv6'}else{'IPv4'}
    [pscustomobject]@{NameServer=($script:Families[$id][$family].Static -join ',')}
}
function Get-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex,[string]$AddressFamily)
    $adapter=@($script:Adapters | Where-Object InterfaceIndex -eq $InterfaceIndex)[0]
    foreach($family in @('IPv4','IPv6')){if(-not $AddressFamily -or $AddressFamily -eq $family){[pscustomobject]@{InterfaceIndex=$InterfaceIndex;Family=$family;ServerAddresses=@($script:Families[$adapter.InterfaceGuid][$family].Servers)}}}
}
function Set-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex,$InputObject,[string[]]$ServerAddresses,[switch]$ResetServerAddresses)
    $script:Mutations++
    if($InterfaceIndex -eq 2 -and $script:FailOnce){$script:FailOnce=$false;throw 'injected second adapter failure'}
    $index=if($InputObject){$InputObject.InterfaceIndex}else{$InterfaceIndex}
    $adapter=@($script:Adapters | Where-Object InterfaceIndex -eq $index)[0]
    $families=if($InputObject){@($InputObject.Family)}else{@('IPv4','IPv6')}
    foreach($family in $families){
        $values=@($ServerAddresses | Where-Object {if($family -eq 'IPv4'){$_ -notmatch ':'}else{$_ -match ':'}})
        if($ResetServerAddresses){$values=@()}
        $script:Families[$adapter.InterfaceGuid][$family]=@{Static=@($values);Servers=@($values)}
    }
}
function Get-DnsClientNrptRule {$script:Rules}
function Add-DnsClientNrptRule { [CmdletBinding()]param([string[]]$Namespace,[string[]]$NameServers,[string]$DisplayName,[string]$Comment,[string]$NameEncoding='Disable',[switch]$PassThru)
    $script:Mutations++
    $rule=[pscustomobject]@{Name=[guid]::NewGuid().ToString('B');Namespace=@($Namespace);NameServers=@($NameServers);DisplayName=$DisplayName;Comment=$Comment;NameEncoding=$NameEncoding;
        DirectAccessEnabled=$false;DnsSecEnabled=$false;DnsSecValidationRequired=$false;DirectAccessQueryIPsecRequired=$false;DnsSecQueryIPsecRequired=$false;IPsecCARestriction=''}
    $script:Rules+=@($rule);if($PassThru){$rule}
}
function Remove-DnsClientNrptRule { [CmdletBinding()]param([string]$Name,[switch]$Force)
    $script:Mutations++;$script:Rules=@($script:Rules | Where-Object Name -ne $Name)
}
function Clear-DnsClientCache { [CmdletBinding()]param() $script:Flushed++ }
function Reset-Fixture {
    $script:TestRoot=Join-Path $script:BaseRoot ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($script:TestRoot)|Out-Null
    $script:Adapters=@([pscustomobject]@{InterfaceGuid='11111111-1111-1111-1111-111111111111';InterfaceIndex=1;Name='Wi Fi';Status='Up'},[pscustomobject]@{InterfaceGuid='22222222-2222-2222-2222-222222222222';InterfaceIndex=2;Name='Ethernet';Status='Up'})
    $script:Families=@{}
    foreach($adapter in $script:Adapters){$script:Families[$adapter.InterfaceGuid]=@{IPv4=@{Static=@();Servers=@('10.0.0.1')};IPv6=@{Static=@('2001:db8::53');Servers=@('2001:db8::53')}}}
    $script:Rules=@();$script:Mutations=0;$script:Flushed=0;$script:FailOnce=$false
    Add-DnsClientNrptRule -Namespace 'aistudio.google.com' -NameServers '1.1.1.1' -DisplayName 'Previous provider' -Comment 'USER_PREVIOUS'
    Add-DnsClientNrptRule -Namespace 'intranet.example' -NameServers '10.0.0.2' -DisplayName 'Company' -Comment 'UNRELATED'
    $script:Mutations=0
}
function Request([string]$Operation='Apply'){
    [pscustomobject]@{JobId=[guid]::NewGuid().ToString('N');Operation=$Operation;Server='185.149.145.6';Preset='Full';ChangeAdapters=$true;UseNrpt=$true;ReplaceExisting=$true;
        Adapters=@($script:Adapters | ForEach-Object {[pscustomobject]@{Index=$_.InterfaceIndex;Guid=$_.InterfaceGuid}})}
}
try {
    Reset-Fixture
    $request=Request;$request.ReplaceExisting=$false
    Reject {Invoke-SystemDns $request} 'Conflicting foreign NRPT was overwritten without opt-in'
    Assert ($script:Mutations -eq 0) 'Preflight changed DNS'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:TestRoot 'state.json'))) 'Preflight created a false active backup'
    $request=Request;$null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Applied') 'Apply status incorrect'
    Assert (@($state.AppliedAdapters).Count -eq 2) 'Adapter backups incomplete'
    Assert (@($script:Rules | Where-Object Comment -eq 'UNRELATED').Count -eq 1) 'Unrelated NRPT rule changed'
    Assert (@($script:Rules | Where-Object Comment -eq $state.Tag).Count -eq 3) 'Owned NRPT rules missing'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static[0] -eq '185.149.145.6') 'DNS not applied'
    $backup=[IO.File]::ReadAllText((Join-Path $script:TestRoot 'state.json'))
    Reject {Invoke-SystemDns (Request)} 'Repeat apply overwrote original baseline'
    Assert ($backup -eq [IO.File]::ReadAllText((Join-Path $script:TestRoot 'state.json'))) 'Baseline changed after repeat apply'
    $null=Invoke-SystemDns (Request Restore);$state=Read-State
    Assert ($state.Status -eq 'Restored') 'Restore status incorrect'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'DHCP restored as static DNS'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv6.Static[0] -eq '2001:db8::53') 'Original static IPv6 DNS lost'
    Assert (@($script:Rules | Where-Object Comment -like 'DEVPROXY*').Count -eq 0) 'Owned NRPT rules remained'
    Assert (@($script:Rules | Where-Object { $_.Comment -eq 'USER_PREVIOUS' -and $_.NameServers -contains '1.1.1.1' }).Count -eq 1) 'Previous NRPT provider not restored'
    Assert (@($script:Rules).Count -eq 2) 'Rollback duplicated rules'
    $null=Invoke-SystemDns (Request Restore);Assert (@($script:Rules).Count -eq 2) 'Repeat rollback duplicated rules'

    Reset-Fixture;$script:FailOnce=$true
    Reject {Invoke-SystemDns (Request)} 'Injected failure was swallowed'
    Assert ((Read-State).Status -eq 'Restored') 'Partial apply not automatically rolled back'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'Partial failure left changed DNS'
    Assert (@($script:Rules | Where-Object Comment -eq 'USER_PREVIOUS').Count -eq 1) 'Partial failure lost previous NRPT'

    Reset-Fixture;$null=Invoke-SystemDns (Request)
    Set-DnsClientServerAddress -InterfaceIndex 1 -ServerAddresses '9.9.9.9'
    Reject {Invoke-SystemDns (Request Restore)} 'External DNS change overwritten'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static[0] -eq '9.9.9.9') 'Foreign DNS edit not preserved'
    Assert ((Read-State).Status -eq 'RollbackFailed') 'Incomplete rollback reported success'

    Reset-Fixture;$null=Invoke-SystemDns (Request)
    $owned=@($script:Rules | Where-Object {$_.Namespace -contains 'cloudcode-pa.googleapis.com'})[0]
    $owned.Namespace=@('daily-cloudcode-pa.googleapis.com')
    Reject {Invoke-SystemDns (Request Restore)} 'Edited NRPT namespace overwritten'
    Assert (@($script:Rules | Where-Object Name -eq $owned.Name).Count -eq 1) 'Foreign NRPT edit not preserved'
    Assert ((Read-State).Status -eq 'RollbackFailed') 'Edited NRPT rollback reported success'

    Reset-Fixture;$script:Rules[0].DnsSecEnabled=$true
    Reject {Invoke-SystemDns (Request)} 'DNSSEC policy replaced'
    Assert ($script:Mutations -eq 0) 'Protected NRPT preflight changed state'

    Reset-Fixture;$request=Request;$request.Adapters[0].Index=999
    Reject {Invoke-SystemDns $request} 'Stale adapter index accepted'
    Assert ($script:Mutations -eq 0) 'Stale adapter request changed DNS'
    $request=Request;$request.Server="185.149.145.6; calc"
    Reject {Invoke-SystemDns $request} 'DNS command injection accepted'
    $request=Request;$request.JobId='../escape'
    Reject {Invoke-SystemDns $request} 'Result path traversal accepted'

    Reset-Fixture
    $savedNrpt=(Get-Item Function:\Get-DnsClientNrptRule).ScriptBlock
    function Get-DnsClientNrptRule {throw 'NRPT cmdlet unavailable in this fixture'}
    try {
        $request=Request;$request.UseNrpt=$false;$request.ReplaceExisting=$false
        $null=Invoke-SystemDns $request
        Assert ((Read-State).Status -eq 'Applied') 'Adapter-only apply requires NRPT'
        Assert (@($script:Rules).Count -eq 2) 'Adapter-only apply changed foreign NRPT'
        $null=Invoke-SystemDns (Request Restore)
        Assert ((Read-State).Status -eq 'Restored') 'Adapter-only rollback requires NRPT'
        Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'Adapter-only rollback lost DHCP'
    } finally {Set-Item Function:\Get-DnsClientNrptRule $savedNrpt}
    "PASS $script:Checks DNS preset, preflight, DHCP/static IPv4/IPv6, NRPT backup, conflict, rollback and validation checks (fixtures only)."
} finally {
    $resolved=[IO.Path]::GetFullPath($script:BaseRoot);$temp=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^DevProxy-systemdns-tests-[0-9a-f]{32}$'){throw 'Invalid test cleanup path'}
    [IO.Directory]::Delete($resolved,$true)
}
