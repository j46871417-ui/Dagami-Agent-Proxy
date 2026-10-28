param([string]$EnginePath='')
$ErrorActionPreference='Stop'
$engine=if($EnginePath){$EnginePath}else{Join-Path $PSScriptRoot 'SystemDns.ps1'}
if(-not (Test-Path -LiteralPath $engine)){$engine=Join-Path $PSScriptRoot '..\..\src\SystemDns.ps1'}
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
if(-not ('DnsJsonTestLock' -as [type])){Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public sealed class DnsJsonTestLock : IDisposable {
    readonly object gate = new object();
    FileStream stream;
    Timer timer;
    public DnsJsonTestLock(string path, int releaseAfterMs) {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (releaseAfterMs >= 0) timer = new Timer(state => Dispose(), null, releaseAfterMs, Timeout.Infinite);
    }
    public void Dispose() { lock(gate) { if(timer != null) { timer.Dispose(); timer = null; } if(stream != null) { stream.Dispose(); stream = null; } } }
}
'@}

# Every network mutator and registry/adapter reader below is a fixture.
# The real Windows DNS cmdlets are never invoked in these tests.
function Assert-Administrator {}
function Protect-Path {param($Path,$Directory)}
function Get-StoreRoot {$script:TestRoot}
function Read-State { $path=Join-Path (Get-StoreRoot) 'state.json';if(Test-Path -LiteralPath $path){Read-SharedText $path | ConvertFrom-Json} }
function Get-ProviderDomains {param($Preset) if($script:RecoveryDomains){return $script:RecoveryDomains};@('aistudio.google.com','cloudcode-pa.googleapis.com','daily-cloudcode-pa.googleapis.com')}
function Get-NetAdapter {param([switch]$IncludeHidden) $script:Adapters}
function Test-FixtureManagedDns {
    @($script:Families.Values | Where-Object {$_.IPv4.Static -contains '185.149.145.6'}).Count -gt 0
}
function Test-DnsProbe {param($HostName,$Server='')
    if($Server){return $script:TargetDnsReachable -and $Server -notin @($script:UnavailableServers)}
    if($script:NoNetworkBaseline){return $false}
    if((Test-FixtureManagedDns) -and $script:BreakDnsAfterApply){$script:PostApplyFailed=$true;return $false}
    if(-not (Test-FixtureManagedDns) -and $script:PostApplyFailed -and $script:RecoveryNetworkBroken){return $false}
    return $true
}
function Test-HttpsProbe {param($Uri)
    if($script:NoNetworkBaseline){return $false}
    if((Test-FixtureManagedDns) -and $script:BreakHttpsAfterApply){$script:PostApplyFailed=$true;return $false}
    if(-not (Test-FixtureManagedDns) -and $script:PostApplyFailed -and $script:RecoveryNetworkBroken){return $false}
    return $true
}
function Test-ServerHttpsProbe {param($Target,$Server) return $script:TargetHttpsReachable}
function Get-ItemProperty {param($LiteralPath,$Name,$ErrorAction)
    $id=[regex]::Match($LiteralPath,'\{([0-9a-fA-F-]+)\}$').Groups[1].Value
    $family=if($LiteralPath -match '\\Tcpip6\\'){'IPv6'}else{'IPv4'}
    [pscustomobject]@{NameServer=($script:Families[$id][$family].Static -join ',')}
}
function Get-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex,[string]$AddressFamily)
    if($InterfaceIndex -eq 37){throw 'No MSFT_DNSClientServerAddress objects with InterfaceIndex 37'}
    foreach($adapter in @($script:Adapters | Where-Object {(-not $InterfaceIndex -or $_.InterfaceIndex -eq $InterfaceIndex) -and $_.InterfaceIndex -ne 37})){
        foreach($family in @('IPv4','IPv6')){if(-not $AddressFamily -or $AddressFamily -eq $family){[pscustomobject]@{InterfaceIndex=$adapter.InterfaceIndex;Family=$family;ServerAddresses=@($script:Families[$adapter.InterfaceGuid][$family].Servers)}}}
    }
}
function Set-DnsClientServerAddress { [CmdletBinding()]param([int]$InterfaceIndex,$InputObject,[string[]]$ServerAddresses,[switch]$ResetServerAddresses)
    $script:Mutations++
    $index=if($InputObject){$InputObject.InterfaceIndex}else{$InterfaceIndex}
    if($InputObject -and $InputObject.Family -eq 'IPv6' -and $script:FailIpv6Once){$script:FailIpv6Once=$false;throw 'injected IPv6 write failure'}
    if($index -eq 2 -and $script:FailOnce){$script:FailOnce=$false;throw 'injected second adapter failure'}
    if($script:IgnoreDnsWrite){return}
    $adapter=@($script:Adapters | Where-Object InterfaceIndex -eq $index)[0]
    $families=if($InputObject){@($InputObject.Family)}else{@('IPv4','IPv6')}
    foreach($family in $families){
        $values=@($ServerAddresses | Where-Object {if($family -eq 'IPv4'){$_ -notmatch ':'}else{$_ -match ':'}})
        # Windows can retain existing DNS in the other family, as the WWAN
        # adapter did in the reported failure. Do not erase it in the fixture.
        if(-not $InputObject -and -not $ResetServerAddresses -and -not $values.Count){continue}
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
    if($script:FailRemove){throw 'injected NRPT remove failure'}
    $script:Mutations++;$script:Rules=@($script:Rules | Where-Object Name -ne $Name)
}
function Clear-DnsClientCache { [CmdletBinding()]param() $script:Flushed++ }
function Backup-ResetNrptStores {param($Directory)
    if($script:FailResetBackup){throw 'injected reset backup failure'}
    $script:SavedResetRules=@($script:Rules)
    @([pscustomobject]@{Key='fixture';File='nrpt-0.reg';Exists=$true})
}
function Clear-ResetNrptStores { $script:Mutations++;if(-not $script:FailRemove){$script:Rules=@()};$script:PolicyPresent=$false }
function Test-ResetNrptStoresEmpty { -not $script:PolicyPresent }
function Restore-ResetNrptStores {param($State) $script:Mutations++;$script:Rules=@($script:SavedResetRules) }
function Get-DnsClientNrptPolicy {param([switch]$Effective,$ErrorAction) if($script:ReapplyPolicy){[pscustomobject]@{Namespace='company.example'}} }
function Reset-Fixture {
    $script:TestRoot=Join-Path $script:BaseRoot ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($script:TestRoot)|Out-Null
    $script:Adapters=@([pscustomobject]@{InterfaceGuid='11111111-1111-1111-1111-111111111111';InterfaceIndex=1;Name='Wi Fi';Status='Up'},[pscustomobject]@{InterfaceGuid='22222222-2222-2222-2222-222222222222';InterfaceIndex=2;Name='Ethernet';Status='Up'})
    $script:Families=@{}
    foreach($adapter in $script:Adapters){$script:Families[$adapter.InterfaceGuid]=@{IPv4=@{Static=@();Servers=@('10.0.0.1')};IPv6=@{Static=@('2001:db8::53');Servers=@('2001:db8::53')}}}
    $script:Rules=@();$script:Mutations=0;$script:Flushed=0;$script:FailOnce=$false;$script:FailRemove=$false;$script:IgnoreDnsWrite=$false;$script:RecoveryDomains=$null
    $script:TargetDnsReachable=$true;$script:TargetHttpsReachable=$true;$script:NoNetworkBaseline=$false;$script:BreakDnsAfterApply=$false;$script:BreakHttpsAfterApply=$false;$script:PostApplyFailed=$false;$script:RecoveryNetworkBroken=$false
    $script:UnavailableServers=@()
    $script:FailIpv6Once=$false
    $script:FailResetBackup=$false;$script:PolicyPresent=$false;$script:ReapplyPolicy=$false;$script:SavedResetRules=@()
    Add-DnsClientNrptRule -Namespace 'aistudio.google.com' -NameServers '1.1.1.1' -DisplayName 'Previous provider' -Comment 'USER_PREVIOUS'
    Add-DnsClientNrptRule -Namespace 'intranet.example' -NameServers '10.0.0.2' -DisplayName 'Company' -Comment 'UNRELATED'
    $script:Mutations=0
}
function Request([string]$Operation='Apply'){
    [pscustomobject]@{JobId=[guid]::NewGuid().ToString('N');Operation=$Operation;Server='185.149.145.6';Preset='Full';ChangeAdapters=$true;UseNrpt=$true;ReplaceExisting=$true;
        Adapters=@($script:Adapters | ForEach-Object {[pscustomobject]@{Index=$_.InterfaceIndex;Guid=$_.InterfaceGuid}})}
}
try {
    # Real NTFS sharing semantics, not a simulated Replace exception.
    $jsonPath=Join-Path $script:BaseRoot 'atomic-state.json'
    Write-Json $jsonPath ([pscustomobject]@{Status='Old'})
    $share=[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $reader=New-Object IO.FileStream($jsonPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,$share)
    try{
        Write-Json $jsonPath ([pscustomobject]@{Status='New'})
        Assert ((Read-SharedText $jsonPath | ConvertFrom-Json).Status -eq 'New') 'Shared status reader blocked atomic replacement'
        $text=New-Object IO.StreamReader($reader)
        try{Assert (($text.ReadToEnd() | ConvertFrom-Json).Status -eq 'Old') 'Atomic replacement overwrote the open old snapshot'}finally{$text.Dispose()}
    }finally{$reader.Dispose()}
    $writer=New-Object IO.FileStream($jsonPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,$share)
    try{Assert ((Read-SharedText $jsonPath | ConvertFrom-Json).Status -eq 'New') 'State reader denies a concurrent write handle'}finally{$writer.Dispose()}

    $held=New-Object DnsJsonTestLock($jsonPath,350)
    try{Write-Json $jsonPath ([pscustomobject]@{Status='Retried'})}finally{$held.Dispose()}
    Assert ((Read-SharedText $jsonPath | ConvertFrom-Json).Status -eq 'Retried') 'Transient sharing violation cancelled a valid state update'
    $prior=Read-SharedText $jsonPath;$held=New-Object DnsJsonTestLock($jsonPath,-1)
    try{
        $errorText='';try{Write-Json $jsonPath ([pscustomobject]@{Status='MustNotAppear'})}catch{$errorText=$_.Exception.Message}
        Assert ($errorText -like '*DNS backup file is busy: atomic-state.json*') 'Persistent lock lacks the file name and actionable error'
        Assert ((Read-SharedText $jsonPath) -eq $prior) 'Persistent lock damaged the original backup'
    }finally{$held.Dispose()}
    Assert (@(Get-ChildItem -LiteralPath $script:BaseRoot -Filter '*.tmp').Count -eq 0) 'Failed state write left temporary files'
    Assert (-not (Test-FileSharingViolation (New-Object UnauthorizedAccessException('fixture denied')))) 'Access errors are retried as sharing violations'

    Reset-Fixture
    $saveStateBody=(Get-Item Function:\Save-State).ScriptBlock
    $script:InjectedStateLock=$false
    function Save-State($State){
        $lock=$null
        try{
            if($State.Status -eq 'Applying' -and -not $script:InjectedStateLock){$script:InjectedStateLock=$true;$lock=New-Object DnsJsonTestLock((Join-Path (Get-StoreRoot) 'state.json'),350)}
            Write-Json (Join-Path (Get-StoreRoot) 'state.json') $State
        }finally{if($lock){$lock.Dispose()}}
    }
    try{
        $null=Invoke-SystemDns (Request)
        Assert ($script:InjectedStateLock -and (Read-State).Status -eq 'Applied') 'Apply still rolls back after a brief state.json reader lock'
    }finally{Set-Item Function:\Save-State $saveStateBody}

    Reset-Fixture
    $script:Adapters+=@([pscustomobject]@{InterfaceGuid='37373737-3737-3737-3737-373737373737';InterfaceIndex=37;Name='Hidden virtual adapter';Status='Disconnected'})
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true
    $result=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Cleared') 'Phantom InterfaceIndex 37 blocked reset'
    Assert (@($state.OriginalAdapters).Count -eq 2 -and @($state.Warnings).Count -eq 1) ('Adapter without DNS bindings was not explicitly skipped: '+($state | ConvertTo-Json -Depth 3 -Compress))
    Assert ($result.Contains('Hidden virtual adapter [37]')) 'Missing DNS binding not shown in reset report'
    Assert (@($script:Rules).Count -eq 0) 'Missing DNS binding prevented NRPT cleanup'

    Reset-Fixture
    $request=Request ResetAll
    Reject {Invoke-SystemDns $request} 'Unconfirmed full reset accepted'
    Assert ($script:Mutations -eq 0) 'Unconfirmed reset changed settings'

    Reset-Fixture;$script:FailResetBackup=$true
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true
    Reject {Invoke-SystemDns $request} 'Reset continued after backup failure'
    Assert ($script:Mutations -eq 0) 'Reset wrote settings before full backup'

    Reset-Fixture;$script:NoNetworkBaseline=$true;$script:PolicyPresent=$true
    $script:Adapters[1].Status='Disconnected'
    $script:Rules[0].DnsSecEnabled=$true
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true
    $request.Server='invalid';$request.Adapters=@();$request.UseNrpt=$false;$request.ChangeAdapters=$false
    $null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Cleared' -and $state.Kind -eq 'ResetAll') 'Full reset did not finish offline'
    Assert (@($state.OriginalAdapters).Count -eq 2) 'Full reset skipped disconnected or unselected adapter'
    Assert (@($script:Rules).Count -eq 0 -and -not $script:PolicyPresent) 'Foreign/DNSSEC/policy NRPT survived reset'
    foreach($adapter in $script:Adapters){foreach($family in @('IPv4','IPv6')){Assert ($script:Families[$adapter.InterfaceGuid][$family].Static.Count -eq 0) 'Full reset did not reset both DNS families'}}
    Assert (Test-Path -LiteralPath (Join-Path $state.ResetDirectory 'snapshot.json')) 'Full reset lost separate original snapshot'
    $script:NoNetworkBaseline=$false;$null=Invoke-SystemDns (Request Restore)
    Assert ((Read-State).Status -eq 'Restored') 'Pre-reset restore failed'
    Assert (@($script:Rules | Where-Object Comment -eq 'UNRELATED').Count -eq 1 -and $script:Rules[0].DnsSecEnabled) 'Pre-reset restore lost foreign/DNSSEC rule fields'
    Assert ($script:Families[$script:Adapters[1].InterfaceGuid].IPv6.Static[0] -eq '2001:db8::53') 'Pre-reset restore lost IPv6 static DNS'

    Reset-Fixture;$script:ReapplyPolicy=$true
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true
    Reject {Invoke-SystemDns $request} 'Full reset falsely succeeded when domain policy returned'
    Assert ((Read-State).Status -eq 'ClearFailed') 'Partial reset not recorded'

    Reset-Fixture;$script:IgnoreDnsWrite=$true
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true
    Reject {Invoke-SystemDns $request} 'Full reset did not verify adapter DNS'
    Assert ((Read-State).Status -eq 'ClearFailed') 'Silent adapter reset failure reported success'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$old=Read-State
    $request=Request ResetAll;$request | Add-Member ConfirmResetAll $true;$null=Invoke-SystemDns $request
    Assert (Test-Path -LiteralPath (Join-Path $script:TestRoot ('history-'+$old.Id+'.json'))) 'Full reset discarded active application backup'
    $request=Request;$request.UseNrpt=$false;$null=Invoke-SystemDns $request
    Assert (@($script:Rules).Count -eq 0) 'Apply after full reset resurrected old foreign NRPT'

    Reset-Fixture;$request=Request ResetAll;$request | Add-Member ConfirmResetAll $true;$null=Invoke-SystemDns $request
    Add-DnsClientNrptRule -Namespace 'new.example' -NameServers '9.9.9.9' -Comment 'NEW_EXTERNAL'
    Reject {Invoke-SystemDns (Request Restore)} 'Reset restore overwrote new external NRPT'
    Assert (@($script:Rules).Count -eq 1 -and $script:Rules[0].Comment -eq 'NEW_EXTERNAL') 'New external NRPT lost during restore'

    Reset-Fixture;$script:TargetDnsReachable=$false
    Reject {Invoke-SystemDns (Request)} 'Unreachable DNS server was applied'
    Assert ($script:Mutations -eq 0) 'Server preflight failure changed DNS or NRPT'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:TestRoot 'state.json'))) 'Server preflight created an active backup'

    Reset-Fixture;$script:TargetHttpsReachable=$false
    Reject {Invoke-SystemDns (Request)} 'DNS answers that break HTTPS were applied'
    Assert ($script:Mutations -eq 0) 'HTTPS preflight failure changed DNS or NRPT'

    Reset-Fixture;$request=Request;$request.Server='45.147.31.32';$script:UnavailableServers=@('45.147.31.32')
    $message=Invoke-SystemDns $request;$state=Read-State
    Assert (($state.Servers -join ',') -eq '46.29.165.108,89.125.103.139,109.120.132.43,94.249.177.102,2.56.175.196') 'Healthy backup servers not selected'
    Assert ($state.Server -eq '46.29.165.108') 'Unavailable primary still selected'
    Assert ($message -match 'Unavailable servers skipped: 45.147.31.32') 'Skipped primary not reported'
    foreach($adapter in $script:Adapters){Assert (($script:Families[$adapter.InterfaceGuid].IPv4.Static -join ',') -eq '46.29.165.108,89.125.103.139,109.120.132.43,94.249.177.102,2.56.175.196') 'Adapter backup list lost'}
    foreach($rule in @($script:Rules | Where-Object Comment -eq $state.Tag)){Assert (($rule.NameServers -join ',') -eq '46.29.165.108,89.125.103.139,109.120.132.43,94.249.177.102,2.56.175.196') 'NRPT backup list lost'}
    $null=Invoke-SystemDns (Request Restore)
    Assert ((Read-State).Status -eq 'Restored') 'Multi-server restore failed'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'Multi-server restore lost DHCP'
    Assert (@($script:Rules | Where-Object Comment -eq 'UNRELATED').Count -eq 1) 'Multi-server restore lost unrelated rules'

    # Encrypted service requests, with every mutator still mocked above.
    Reset-Fixture;$request=Request;$request.Server='127.0.0.1';$request.ChangeAdapters=$false;$request.Adapters=@();$request | Add-Member EncryptedStub $true
    $null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.EncryptedStub -and $state.Status -eq 'Applied') 'Targeted encrypted DNS not recorded'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Servers[0] -eq '10.0.0.1') 'Targeted encrypted DNS changed adapter settings'
    Assert (-not (Test-NoLocalDnsReferences)) 'Active NRPT loopback route was not detected'
    $null=Invoke-SystemDns (Request Restore)
    Assert (Test-NoLocalDnsReferences) 'Restored targeted mode left loopback references'
    Add-DnsClientNrptRule -Namespace 'external.example' -NameServers '::1' -Comment 'EXTERNAL_LOCAL'
    Assert (-not (Test-NoLocalDnsReferences)) 'External local resolver reference ignored when deciding to stop service'

    Reset-Fixture;$request=Request;$request.Server='127.0.0.1';$request | Add-Member EncryptedStub $true
    Reject {Invoke-SystemDns $request} 'Whole-network mode accepted without verified IPv6 listener'
    Assert ($script:Mutations -eq 0) 'Missing IPv6 service changed network settings'
    $request | Add-Member StubIpv6 $true;$null=Invoke-SystemDns $request;$state=Read-State
    Assert (($state.ManagedFamilies -join ',') -eq 'IPv4,IPv6') 'Encrypted whole-network mode did not manage both families'
    foreach($adapter in $script:Adapters){Assert ($script:Families[$adapter.InterfaceGuid].IPv4.Static[0] -eq '127.0.0.1' -and $script:Families[$adapter.InterfaceGuid].IPv6.Static[0] -eq '::1') 'Whole-network mode leaked the other DNS family'}
    $null=Invoke-SystemDns (Request Restore)
    foreach($adapter in $script:Adapters){Assert ($script:Families[$adapter.InterfaceGuid].IPv4.Static.Count -eq 0 -and $script:Families[$adapter.InterfaceGuid].IPv6.Static[0] -eq '2001:db8::53') 'Encrypted restore lost DHCP or static IPv6'}
    Assert (Test-NoLocalDnsReferences) 'Encrypted whole-network restore left loopback references'

    Reset-Fixture;$request=Request;$request.Server='127.0.0.1';$request | Add-Member EncryptedStub $true;$request | Add-Member StubIpv6 $true;$script:FailIpv6Once=$true
    Reject {Invoke-SystemDns $request} 'Partial encrypted IPv6 write failure reported success'
    Assert ((Read-State).Status -eq 'Restored') 'Encrypted write failure did not roll back'
    foreach($adapter in $script:Adapters){Assert ($script:Families[$adapter.InterfaceGuid].IPv4.Static.Count -eq 0 -and $script:Families[$adapter.InterfaceGuid].IPv6.Static[0] -eq '2001:db8::53') 'Partial encrypted write left a changed family'}

    Reset-Fixture;$request=Request;$request.Server='45.147.31.32';$script:TargetDnsReachable=$false
    Reject {Invoke-SystemDns $request} 'All failed DNS nodes were applied'
    Assert ($script:Mutations -eq 0) 'All-node failure changed system settings'

    Reset-Fixture;$request=Request;$request | Add-Member Servers @('185.149.145.6','2001:db8::53')
    Reject {Invoke-SystemDns $request} 'Mixed address families accepted'
    Assert ($script:Mutations -eq 0) 'Invalid server list changed system settings'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$state=Read-State
    $state.PSObject.Properties.Remove('Servers');$state.Version=1;Save-State $state
    $null=Invoke-SystemDns (Request Restore)
    Assert ((Read-State).Status -eq 'Restored') 'Legacy single-server backup cannot be restored'

    Reset-Fixture;$script:NoNetworkBaseline=$true
    Reject {Invoke-SystemDns (Request)} 'Unverifiable initial network was modified'
    Assert ($script:Mutations -eq 0) 'Baseline failure changed the network'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$prior=Read-State;$backup=[IO.File]::ReadAllText((Join-Path $script:TestRoot 'state.json'));$mutations=$script:Mutations
    $script:TargetDnsReachable=$false
    $request=Request;$request | Add-Member ReplaceActiveSetup $true;$request | Add-Member KeepExternalChanges $true
    Reject {Invoke-SystemDns $request} 'Unreachable new DNS server replaced a working setup'
    Assert ($script:Mutations -eq $mutations) 'Reapply preflight rolled back the working network'
    Assert ([IO.File]::ReadAllText((Join-Path $script:TestRoot 'state.json')) -eq $backup) 'Reapply preflight overwrote the active backup'

    foreach($failureKind in @('Dns','Https')){
        Reset-Fixture
        if($failureKind -eq 'Dns'){$script:BreakDnsAfterApply=$true}else{$script:BreakHttpsAfterApply=$true}
        Reject {Invoke-SystemDns (Request)} ($failureKind+' outage after DNS write was reported as success')
        $state=Read-State
        Assert ($state.Status -eq 'Restored') ($failureKind+' outage left managed DNS active')
        Assert ($state.RecoveryConnectivityVerified -eq $true) ($failureKind+' rollback did not check network recovery')
        Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) ($failureKind+' outage did not restore DHCP')
        Assert (@($script:Rules | Where-Object Comment -eq 'USER_PREVIOUS').Count -eq 1) ($failureKind+' outage lost previous NRPT')
    }

    Reset-Fixture;$script:BreakHttpsAfterApply=$true;$script:RecoveryNetworkBroken=$true
    $message='';try{$null=Invoke-SystemDns (Request)}catch{$message=$_.Exception.Message}
    Assert ($message -like '*Previous settings restored, but connectivity could not be verified:*') 'Unverified network after rollback falsely reported healthy'
    Assert ((Read-State).Status -eq 'Restored' -and (Read-State).RecoveryConnectivityVerified -eq $false) 'Rollback network failure lost the saved settings outcome'

    Reset-Fixture
    $request=Request;$request.ReplaceExisting=$false
    Reject {Invoke-SystemDns $request} 'Conflicting foreign NRPT was overwritten without opt-in'
    Assert ($script:Mutations -eq 0) 'Preflight changed DNS'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:TestRoot 'state.json'))) 'Preflight created a false active backup'
    $request=Request;$null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Applied') 'Apply status incorrect'
    Assert ($state.ConnectivityVerified -eq $true -and @($state.ConnectivityTargets).Count -ge 2) 'Apply committed without DNS and HTTPS verification'
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

    # Mobile broadband retained two static IPv6 resolvers after a valid IPv4
    # update. Only the selected server's address family should be verified.
    Reset-Fixture
    $mobile=$script:Adapters[1];$mobile.Name='Mobile broadband';$id=$mobile.InterfaceGuid
    $script:Families[$id].IPv4=@{Static=@('192.0.2.53');Servers=@('192.0.2.53')}
    $script:Families[$id].IPv6=@{Static=@('2001:db8:1::53','2001:db8:2::53');Servers=@('2001:db8:1::53','2001:db8:2::53')}
    $request=Request;$request.UseNrpt=$false;$request.Adapters=@([pscustomobject]@{Index=2;Guid=$id})
    $message=Invoke-SystemDns $request
    Assert ((Read-State).Status -eq 'Applied' -and (Read-State).ServerFamily -eq 'IPv4') 'Mobile IPv6 resolvers rejected a valid IPv4 DNS write'
    Assert ($script:Families[$id].IPv4.Static[0] -eq $request.Server) 'Mobile IPv4 DNS not set'
    Assert (($script:Families[$id].IPv6.Static -join ',') -eq '2001:db8:1::53,2001:db8:2::53') 'IPv4 DNS write erased carrier IPv6 DNS'
    Assert ($message -match 'Family: IPv4\.') 'IPv4 apply report omitted its scope'
    $null=Invoke-SystemDns (Request Restore)
    Assert ($script:Families[$id].IPv4.Static[0] -eq '192.0.2.53') 'Mobile original IPv4 DNS not restored'
    Assert (($script:Families[$id].IPv6.Static -join ',') -eq '2001:db8:1::53,2001:db8:2::53') 'Mobile rollback altered untouched IPv6 DNS'

    Reset-Fixture;$id=$script:Adapters[0].InterfaceGuid
    $request=Request;$request.Server='2001:db8::6';$request.UseNrpt=$false
    $message=Invoke-SystemDns $request
    Assert ((Read-State).ServerFamily -eq 'IPv6' -and (Read-State).Status -eq 'Applied') 'IPv6 DNS write rejected due to IPv4 resolvers'
    Assert ($script:Families[$id].IPv4.Static.Count -eq 0 -and $script:Families[$id].IPv4.Servers[0] -eq '10.0.0.1') 'IPv6 DNS write changed IPv4 DHCP DNS'
    Assert ($script:Families[$id].IPv6.Static[0] -eq '2001:db8::6') 'IPv6 DNS not set'
    $null=Invoke-SystemDns (Request Restore)
    Assert ($script:Families[$id].IPv6.Static[0] -eq '2001:db8::53') 'IPv6 baseline not restored'

    Reset-Fixture;$script:IgnoreDnsWrite=$true
    Reject {Invoke-SystemDns (Request)} 'Verification accepted a failed write to the selected family'
    Assert ((Read-State).Status -eq 'Restored') 'Failed target-family write did not roll back'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$prior=Read-State
    $request=Request;$request.Server='8.8.4.4';$request | Add-Member ReplaceActiveSetup $true;$request | Add-Member KeepExternalChanges $true
    $null=Invoke-SystemDns $request
    Assert ((Read-State).Status -eq 'Applied' -and (Read-State).Server -eq '8.8.4.4') 'One-request reapply still blocked by active backup'
    $history=Get-Content -LiteralPath (Join-Path $script:TestRoot ('history-'+$prior.Id+'.json')) -Raw | ConvertFrom-Json
    Assert ($history.Status -eq 'Restored') 'Reapply did not archive the prior setup'
    Assert ((Read-State).OriginalAdapters[0].Families[0].Source -eq 'Dhcp') 'Reapply captured old managed DNS instead of baseline'
    $null=Invoke-SystemDns (Request Restore)
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'Reapply rollback lost original DHCP mode'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$prior=Read-State;$mutations=$script:Mutations
    $request=Request;$request.Adapters[0].Index=999;$request | Add-Member ReplaceActiveSetup $true;$request | Add-Member KeepExternalChanges $true
    Reject {Invoke-SystemDns $request} 'Reapply accepted stale adapter selection'
    Assert ($script:Mutations -eq $mutations -and (Read-State).Status -eq 'Applied') 'Invalid reapply touched the current setup'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$prior=Read-State;$script:FailRemove=$true
    $request=Request;$request.Server='8.8.4.4';$request | Add-Member ReplaceActiveSetup $true;$request | Add-Member KeepExternalChanges $true
    Reject {Invoke-SystemDns $request} 'Reapply ignored a real NRPT removal failure'
    Assert ((Read-State).Id -eq $prior.Id -and (Read-State).Status -eq 'RollbackFailed') 'Failed reapply discarded the active backup'

    Reset-Fixture;$script:FailOnce=$true
    Reject {Invoke-SystemDns (Request)} 'Injected failure was swallowed'
    Assert ((Read-State).Status -eq 'Restored') 'Partial apply not automatically rolled back'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static.Count -eq 0) 'Partial failure left changed DNS'
    Assert (@($script:Rules | Where-Object Comment -eq 'USER_PREVIOUS').Count -eq 1) 'Partial failure lost previous NRPT'

    # Two old rules for one domain caused one false foreign-rule error per
    # duplicate, leaving the backup active after every retry.
    Reset-Fixture
    Add-DnsClientNrptRule -Namespace 'aistudio.google.com' -NameServers '8.8.8.8' -DisplayName 'Second previous rule' -Comment 'SECOND_PREVIOUS'
    $null=Invoke-SystemDns (Request)
    $null=Invoke-SystemDns (Request Restore)
    Assert ((Read-State).Status -eq 'Restored') 'Overlapping baseline NRPT rules block rollback'
    Assert (@($script:Rules).Count -eq 3) 'Overlapping baseline rules were lost or duplicated'
    Assert (@($script:Rules | Where-Object Comment -eq 'SECOND_PREVIOUS').Count -eq 1) 'Second baseline rule not restored'
    $null=Invoke-SystemDns (Request Restore)
    Assert (@($script:Rules).Count -eq 3) 'Retry duplicated overlapping baseline rules'

    # Resume an old RollbackFailed state that already restored the first rule.
    Reset-Fixture
    Add-DnsClientNrptRule -Namespace 'aistudio.google.com' -NameServers '8.8.8.8' -Comment 'SECOND_PREVIOUS'
    $null=Invoke-SystemDns (Request);$state=Read-State
    foreach($rule in @($script:Rules | Where-Object Comment -eq $state.Tag)){Remove-DnsClientNrptRule -Name $rule.Name}
    $restored=Restore-Rule $state.OriginalRules[0];$state.OriginalRules[0].RestoredName=$restored.Name
    $state.Status='RollbackFailed';Save-State $state
    $null=Invoke-SystemDns (Request Restore)
    Assert ((Read-State).Status -eq 'Restored') 'Old partial rollback cannot resume'
    Assert (@($script:Rules).Count -eq 3) 'Old partial rollback duplicated rules'

    Reset-Fixture;$null=Invoke-SystemDns (Request)
    Set-DnsClientServerAddress -InterfaceIndex 1 -ServerAddresses '9.9.9.9'
    Reject {Invoke-SystemDns (Request Restore)} 'External DNS change overwritten'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static[0] -eq '9.9.9.9') 'Foreign DNS edit not preserved'
    Assert ((Read-State).Status -eq 'RollbackFailed') 'Incomplete rollback reported success'

    # IPv4 still belongs to this operation even if IPv6 changed elsewhere.
    Reset-Fixture;$null=Invoke-SystemDns (Request)
    $id=$script:Adapters[0].InterfaceGuid
    $script:Families[$id].IPv6=@{Static=@('2001:db8::99');Servers=@('2001:db8::99')}
    $null=Invoke-SystemDns (Request Restore)
    Assert ($script:Families[$id].IPv4.Static.Count -eq 0) 'External IPv6 edit blocked owned IPv4 restore'
    Assert ($script:Families[$id].IPv6.Static[0] -eq '2001:db8::99') 'External IPv6 edit lost'
    $request=Request Restore;$request | Add-Member KeepExternalChanges $true
    $message=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Restored') 'Unmanaged IPv6 edit blocked owned IPv4 restoration'
    Assert (@($state.Warnings).Count -eq 0) 'Unmanaged address family incorrectly treated as owned'
    Assert ($message -eq 'Previous DNS settings are already restored.') 'Completed family-scoped restore changed on retry'
    $null=Invoke-SystemDns $request
    Assert ($script:Families[$id].IPv6.Static[0] -eq '2001:db8::99') 'Repeated preserve rollback changed DNS'
    $null=Invoke-SystemDns (Request)
    $history=Join-Path $script:TestRoot ('history-'+$state.Id+'.json')
    Assert (Test-Path -LiteralPath $history) 'Released backup not archived before apply'
    $saved=Get-Content -LiteralPath $history -Raw | ConvertFrom-Json
    Assert ($saved.OriginalAdapters[0].Families[1].Servers[0] -eq '2001:db8::53') 'Original backup lost after new apply'

    Reset-Fixture;$null=Invoke-SystemDns (Request)
    $script:Adapters=@($script:Adapters | Where-Object InterfaceIndex -ne 2)
    Reject {Invoke-SystemDns (Request Restore)} 'Missing device silently dropped from strict restore'
    $request=Request Restore;$request | Add-Member KeepExternalChanges $true
    $null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Released') 'Missing device blocks explicit preserve mode'
    Assert (@($state.OriginalAdapters).Count -eq 2) 'Missing device backup discarded'

    Reset-Fixture;$null=Invoke-SystemDns (Request)
    Add-DnsClientNrptRule -Namespace 'aistudio.google.com' -NameServers '9.9.9.9' -Comment 'NEW_FOREIGN'
    Reject {Invoke-SystemDns (Request Restore)} 'Foreign overlapping NRPT rule overwritten'
    $request=Request Restore;$request | Add-Member KeepExternalChanges $true
    $null=Invoke-SystemDns $request
    Assert ((Read-State).Status -eq 'Released') 'Foreign NRPT prevents explicit preserve mode'
    Assert (@($script:Rules | Where-Object Comment -eq 'NEW_FOREIGN').Count -eq 1) 'Foreign NRPT removed in preserve mode'
    Assert (@($script:Rules | Where-Object Comment -eq 'USER_PREVIOUS').Count -eq 0) 'Old NRPT reintroduced over foreign rule'

    Reset-Fixture;$null=Invoke-SystemDns (Request);$script:FailRemove=$true
    $request=Request Restore;$request | Add-Member KeepExternalChanges $true
    Reject {Invoke-SystemDns $request} 'Preserve mode swallowed a DNS cmdlet failure'
    Assert ((Read-State).Status -eq 'RollbackFailed') 'Failed NRPT removal released active backup'

    # The reported shape: 62 old rules over 31 namespaces, 31 already
    # restored, two external adapter edits and two devices now missing.
    Reset-Fixture
    $script:RecoveryDomains=@(1..31 | ForEach-Object {"nrpt$_.example"});$script:Rules=@()
    foreach($domain in $script:RecoveryDomains){
        Add-DnsClientNrptRule -Namespace $domain -NameServers '1.1.1.1' -Comment 'PREVIOUS_A'
        Add-DnsClientNrptRule -Namespace $domain -NameServers '8.8.8.8' -Comment 'PREVIOUS_B'
    }
    foreach($index in 3..4){
        $id=([guid]::NewGuid()).ToString()
        $script:Adapters+=@([pscustomobject]@{InterfaceGuid=$id;InterfaceIndex=$index;Name="Removed $index";Status='Up'})
        $script:Families[$id]=@{IPv4=@{Static=@();Servers=@('10.0.0.1')};IPv6=@{Static=@();Servers=@()}}
    }
    $null=Invoke-SystemDns (Request);$state=Read-State
    foreach($rule in @($script:Rules)) {Remove-DnsClientNrptRule -Name $rule.Name}
    for($index=0;$index -lt 62;$index+=2){
        $rule=Restore-Rule $state.OriginalRules[$index];$state.OriginalRules[$index].RestoredName=$rule.Name
    }
    $state.Status='RollbackFailed';Save-State $state
    $script:Adapters=@($script:Adapters | Where-Object InterfaceIndex -lt 3)
    foreach($adapter in $script:Adapters){Set-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -ServerAddresses '9.9.9.9'}
    Reject {Invoke-SystemDns (Request Restore)} 'Reported case silently discarded external edits'
    Assert (@((Read-State).Errors).Count -eq 4) 'Duplicate baseline rules still treated as foreign'
    Assert (@($script:Rules).Count -eq 62) 'Reported case lost baseline NRPT rules'
    $request=Request Restore;$request | Add-Member KeepExternalChanges $true
    $null=Invoke-SystemDns $request;$state=Read-State
    Assert ($state.Status -eq 'Released') 'Reported case still blocks new apply'
    Assert (@($state.Warnings).Count -eq 4) 'Reported case skips not fully reported'
    Assert (@($state.OriginalRules).Count -eq 62 -and @($state.OriginalAdapters).Count -eq 4) 'Reported case lost recovery baseline'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static[0] -eq '9.9.9.9') 'Reported case overwrote external DNS'
    $mutations=$script:Mutations;$null=Invoke-SystemDns $request
    Assert ($script:Mutations -eq $mutations) 'Released rollback retry changed DNS'

    # The GUI now handles the same old RollbackFailed state while applying a
    # fresh selection, in one elevated helper. VMware is not selected again.
    $state.Status='RollbackFailed';Save-State $state
    $id=([guid]::NewGuid()).ToString()
    $script:Adapters+=@([pscustomobject]@{InterfaceGuid=$id;InterfaceIndex=5;Name='Main network';Status='Up'})
    $script:Families[$id]=@{IPv4=@{Static=@();Servers=@('10.0.0.1')};IPv6=@{Static=@();Servers=@()}}
    $request=Request;$request.UseNrpt=$false;$request.Adapters=@([pscustomobject]@{Index=5;Guid=$id})
    $request | Add-Member ReplaceActiveSetup $true;$request | Add-Member KeepExternalChanges $true
    $message=Invoke-SystemDns $request;$current=Read-State
    Assert ($current.Status -eq 'Applied' -and @($current.OriginalAdapters).Count -eq 1) 'Reported case still needs separate manual restore before apply'
    Assert (@($current.PreviousWarnings).Count -eq 4) 'Automatic recovery hid VMware or missing-device skips'
    $history=Get-Content -LiteralPath (Join-Path $script:TestRoot ('history-'+$state.Id+'.json')) -Raw | ConvertFrom-Json
    Assert ($history.Status -eq 'Released' -and @($history.OriginalRules).Count -eq 62) 'Automatic recovery lost the old NRPT baseline'
    Assert ($script:Families[$script:Adapters[0].InterfaceGuid].IPv4.Static[0] -eq '9.9.9.9') 'Automatic recovery touched unselected VMware DNS'
    Assert ($script:Families[$id].IPv4.Static[0] -eq '185.149.145.6') 'Automatic recovery did not apply DNS to the new selection'
    Assert ($message -match 'Previous setup closed with preserved external settings:') 'Apply report omitted automatic recovery details'

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
