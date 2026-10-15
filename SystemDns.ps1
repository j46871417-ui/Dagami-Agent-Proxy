param([ValidateSet('Apply','Restore','Status')][string]$Operation='Status', [string]$RequestJson='{}', [string]$RequestBase64='', [switch]$LibraryOnly)
$ErrorActionPreference='Stop'
if($RequestBase64){$RequestJson=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64))}

function Get-StoreRoot { Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'DevProxy-SystemDns' }
function Assert-Administrator {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator rights are required.'}
}
function Protect-Path([string]$Path,[bool]$Directory=$false) {
    if(([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse points are not supported in the DNS backup store.'}
    $admins=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system=New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $users=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    if($Directory){$acl=New-Object Security.AccessControl.DirectorySecurity;$inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}
    else{$acl=New-Object Security.AccessControl.FileSecurity;$inherit=[Security.AccessControl.InheritanceFlags]::None}
    $acl.SetOwner($admins);$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @($admins,$system)){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl',$inherit,'None','Allow')))}
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users,'ReadAndExecute',$inherit,'None','Allow')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Initialize-Store {
    $root=Get-StoreRoot
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Protect-Path $root $true
    [IO.Directory]::CreateDirectory((Join-Path $root 'jobs')) | Out-Null
    Protect-Path (Join-Path $root 'jobs') $true
}
function Write-Json([string]$Path,$Value) {
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $json=$Value | ConvertTo-Json -Depth 16
        [IO.File]::WriteAllText($temp,$json,(New-Object Text.UTF8Encoding($false)))
        Protect-Path $temp
        if([IO.File]::ReadAllText($temp) -ne $json){throw 'DNS backup integrity check failed.'}
        if([IO.File]::Exists($Path)){[IO.File]::Replace($temp,$Path,[NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Read-State {
    $path=Join-Path (Get-StoreRoot) 'state.json'
    if(-not (Test-Path -LiteralPath $path)){return $null}
    if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Invalid backup file.'}
    $owner=(Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner -notin @('S-1-5-32-544','S-1-5-18')){throw 'The DNS backup is not administrator-owned. It will not be used.'}
    Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Save-State($State){Write-Json (Join-Path (Get-StoreRoot) 'state.json') $State}
function Get-ProviderDomains([string]$Preset) {
    # Generated from the user's setup_smartdns_windows.ps1, plus the two
    # Antigravity API names already supported by the native bridge.
    $all=@(
        'gemini.google.com',
        'gemini.google',
        'bard.google.com',
        'aistudio.google.com',
        'makersuite.google.com',
        'ai.google.dev',
        'deepmind.google',
        'deepmind.com',
        'generativeai.google',
        'apis.google.com',
        'alkalimakersuite-pa.clients6.google.com',
        'alkalicore-pa.clients6.google.com',
        'geminiweb-pa.clients6.google.com',
        'waa-pa.clients6.google.com',
        'generativelanguage.googleapis.com',
        'proactivebackend-pa.googleapis.com',
        'robinfrontend-pa.googleapis.com',
        'aisandbox-pa.googleapis.com',
        'notebook.google.com',
        'notebooklm.google.com',
        'flow.google.com',
        'jules.google.com',
        'labs.google',
        '.clients6.google.com',
        'grok.com',
        'x.ai',
        'cdn.grok.com',
        'chatgpt.com',
        'ab.chatgpt.com',
        'api.chatgpt.com',
        'webrtc.chatgpt.com',
        'ws.chatgpt.com',
        'openai.com',
        'api.openai.com',
        'auth.openai.com',
        'oaistatic.com',
        'oaiusercontent.com',
        'featuregates.org',
        'statsigapi.net',
        'statsig.com',
        'claude.ai',
        'www.claude.ai',
        'claude.com',
        'platform.claude.com',
        'api.anthropic.com',
        'anthropic.com',
        'copilot.microsoft.com',
        'copilot.com',
        'www.copilot.com',
        'sydney.bing.com',
        'xboxlive.com',
        'live.com',
        'xbox.com',
        'passport.net',
        'msftidentity.com',
        'msftauth.net',
        'microsoftonline.com',
        'microsoft.com',
        'perplexity.ai',
        'strava.com',
        'www.strava.com',
        'cdn-1.strava.com',
        'graphql.strava.com',
        'twitch.tv',
        'gql.twitch.tv',
        'ttvnw.net',
        'usher.ttvnw.net',
        'challenges.cloudflare.com'
    ) + @('cloudcode-pa.googleapis.com','daily-cloudcode-pa.googleapis.com',
        'accounts.google.com','oauth2.googleapis.com','www.googleapis.com',
        'lh3.googleusercontent.com','www.gstatic.com','ssl.gstatic.com')
    if($Preset -eq 'Full'){return @($all | Select-Object -Unique)}
    if($Preset -ne 'Development'){throw 'Unknown DNS preset.'}
    @($all | Where-Object {$_ -notmatch '^(strava\.com|www\.strava\.com|cdn-1\.strava\.com|graphql\.strava\.com|twitch\.tv|gql\.twitch\.tv|ttvnw\.net|usher\.ttvnw\.net|xboxlive\.com|xbox\.com)$'} | Select-Object -Unique)
}
function Validate-Request($Request) {
    $job=[guid]::Empty
    if(-not [guid]::TryParseExact([string]$Request.JobId,'N',[ref]$job)){throw 'Invalid DNS job ID.'}
    if($Request.Operation -notin @('Apply','Restore')){throw 'Invalid DNS operation.'}
    if($Request.Operation -eq 'Restore'){return}
    $ip=$null
    if(-not [Net.IPAddress]::TryParse([string]$Request.Server,[ref]$ip)){throw 'Use a literal DNS server IP address.'}
    $Request.Server=$ip.ToString()
    if(@($Request.Adapters).Count -gt 64){throw 'Too many network adapters.'}
    if(-not $Request.ChangeAdapters -and -not $Request.UseNrpt){throw 'No DNS changes selected.'}
    if($Request.ChangeAdapters -and @($Request.Adapters).Count -eq 0){throw 'Select at least one adapter.'}
    foreach($adapter in @($Request.Adapters)){
        $id=[guid]::Empty
        if([int]$adapter.Index -lt 1 -or -not [guid]::TryParse([string]$adapter.Guid,[ref]$id)){throw 'Invalid adapter identity.'}
    }
    $null=Get-ProviderDomains ([string]$Request.Preset)
}
function Find-Adapter([string]$Guid) {
    $found=@(Get-NetAdapter -IncludeHidden | Where-Object {([guid]$_.InterfaceGuid).ToString() -eq ([guid]$Guid).ToString()})
    if($found.Count -ne 1){throw "Adapter is missing: $Guid"}
    $found[0]
}
function Read-Family($Adapter,[string]$Family) {
    $service=if($Family -eq 'IPv4'){'Tcpip'}else{'Tcpip6'}
    $guid=([guid]$Adapter.InterfaceGuid).ToString('B')
    $key='HKLM:\SYSTEM\CurrentControlSet\Services\'+$service+'\Parameters\Interfaces\'+$guid
    $item=Get-ItemProperty -LiteralPath $key -Name NameServer -ErrorAction SilentlyContinue
    $value=if($item){$item.NameServer}else{''}
    if($null -ne $value -and $value -isnot [string]){throw 'Unsupported DNS registry value; no changes made.'}
    $static=@(([string]$value -split '[,;\s]+') | Where-Object {$_} | Select-Object -Unique)
    foreach($server in $static){$ip=$null;if(-not [Net.IPAddress]::TryParse($server,[ref]$ip)){throw 'Invalid saved static DNS address.'}}
    $source=if($static.Count){'Static'}else{'Dhcp'}
    [pscustomobject]@{Family=$Family;Source=$source;Servers=$static}
}
function Read-Adapter($Adapter) {
    [pscustomobject]@{Guid=([guid]$Adapter.InterfaceGuid).ToString();Index=[int]$Adapter.InterfaceIndex;Alias=[string]$Adapter.Name;
        Families=@((Read-Family $Adapter 'IPv4'),(Read-Family $Adapter 'IPv6'))}
}
function Adapter-Fingerprint($Snapshot) {
    (@($Snapshot.Families | ForEach-Object {$_.Family+':'+$_.Source+':'+(@($_.Servers)-join ',')}) -join '|')
}
function Snapshot-Rule($Rule) {
    if($Rule.DirectAccessEnabled -or $Rule.DnsSecEnabled -or $Rule.DnsSecValidationRequired -or
        $Rule.DirectAccessQueryIPsecRequired -or $Rule.DnsSecQueryIPsecRequired -or $Rule.IPsecCARestriction){
        throw 'A DNSSEC/DirectAccess/IPsec NRPT rule overlaps. It cannot be replaced automatically.'
    }
    [pscustomobject]@{OriginalName=[string]$Rule.Name;Namespace=@($Rule.Namespace | ForEach-Object {[string]$_});
        NameServers=@($Rule.NameServers | ForEach-Object {[string]$_});DisplayName=[string]$Rule.DisplayName;
        Comment=[string]$Rule.Comment;NameEncoding=[string]$Rule.NameEncoding;RestoredName=$null}
}
function Restore-Rule($Rule) {
    $parameters=@{Namespace=@($Rule.Namespace);NameServers=@($Rule.NameServers);DisplayName=[string]$Rule.DisplayName;Comment=[string]$Rule.Comment;PassThru=$true;ErrorAction='Stop'}
    if($Rule.NameEncoding){$parameters.NameEncoding=[string]$Rule.NameEncoding}
    Add-DnsClientNrptRule @parameters
}
function Rule-Fingerprint($Rule) {
    (@($Rule.Namespace)-join ',')+'|'+(@($Rule.NameServers | ForEach-Object {[string]$_})-join ',')+'|'+$Rule.DisplayName+'|'+$Rule.Comment+'|'+$Rule.NameEncoding
}
function New-Plan($Request) {
    $adapters=@()
    if($Request.ChangeAdapters){foreach($selected in @($Request.Adapters)){
        $adapter=Find-Adapter ([string]$selected.Guid)
        if([int]$adapter.InterfaceIndex -ne [int]$selected.Index -or [string]$adapter.Status -ne 'Up'){throw 'Adapter state changed; refresh the adapter list.'}
        $adapters+=Read-Adapter $adapter
    }}
    $replace=@();$add=@();$reused=0
    if($Request.UseNrpt){
        $domains=@(Get-ProviderDomains ([string]$Request.Preset));$existing=@(Get-DnsClientNrptRule)
        foreach($domain in $domains){
            $matches=@($existing | Where-Object {$_.Namespace -contains $domain})
            if($matches.Count -eq 1 -and @($matches[0].NameServers).Count -eq 1 -and [string]$matches[0].NameServers -eq [string]$Request.Server){$reused++;continue}
            if($matches.Count -and -not $Request.ReplaceExisting){throw "Conflicting NRPT rule for $domain. Enable replacement with backup or disable NRPT changes."}
            foreach($rule in $matches){
                if(@($rule.Namespace | Where-Object {$_ -notin $domains}).Count){throw 'An overlapping rule also covers domains outside this preset. It will not be changed.'}
                if(-not @($replace | Where-Object {$_.OriginalName -eq [string]$rule.Name}).Count){$replace+=Snapshot-Rule $rule}
            }
            $add+=$domain
        }
    }
    [pscustomobject]@{Version=1;Status='Prepared';Id=[string]$Request.JobId;Server=[string]$Request.Server;Preset=[string]$Request.Preset;
        ChangeAdapters=[bool]$Request.ChangeAdapters;UseNrpt=[bool]$Request.UseNrpt;ReplaceExisting=[bool]$Request.ReplaceExisting;
        OriginalAdapters=$adapters;AppliedAdapters=@();PendingAdapter=$null;OriginalRules=$replace;
        RemovedRuleNames=@();PendingRemove=$null;NewNamespaces=$add;CreatedRules=@();ReusedRules=$reused;Tag=('DEVPROXY_SMARTDNS_V1:'+([string]$Request.JobId));Errors=@()}
}
function Restore-Adapter($Snapshot) {
    $adapter=Find-Adapter ([string]$Snapshot.Guid)
    foreach($family in @($Snapshot.Families)){
        $input=Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily $family.Family -ErrorAction Stop
        if($family.Source -eq 'Dhcp'){Set-DnsClientServerAddress -InputObject $input -ResetServerAddresses -ErrorAction Stop}
        else{Set-DnsClientServerAddress -InputObject $input -ServerAddresses @($family.Servers) -ErrorAction Stop}
    }
    $actual=Read-Adapter $adapter
    if((Adapter-Fingerprint $actual) -ne (Adapter-Fingerprint $Snapshot)){throw ('DNS restore verification failed for '+$Snapshot.Alias)}
}
function Undo-State($State,[bool]$FailureRollback=$false) {
    $errors=New-Object 'Collections.Generic.List[string]'
    foreach($original in @($State.OriginalAdapters)){
        $applied=@($State.AppliedAdapters | Where-Object {$_.Guid -eq $original.Guid})
        if(-not $applied.Count -and $State.PendingAdapter -ne $original.Guid){continue}
        try {
            $current=Read-Adapter (Find-Adapter $original.Guid)
            if((Adapter-Fingerprint $current) -eq (Adapter-Fingerprint $original)){continue}
            if(-not $FailureRollback -and $applied.Count -and (Adapter-Fingerprint $current) -ne (Adapter-Fingerprint $applied[0])){throw ('DNS changed outside DevProxy: '+$original.Alias)}
            Restore-Adapter $original
        }catch{$errors.Add($_.Exception.Message)}
    }
    $rules=@()
    if($State.UseNrpt -or @($State.CreatedRules).Count -or @($State.OriginalRules).Count){$rules=@(Get-DnsClientNrptRule)}
    $blocked=@()
    foreach($created in @($State.CreatedRules)){
        $found=@($rules | Where-Object {$_.Name -eq $created.Name})
        if($found.Count -and ($found[0].Comment -ne $State.Tag -or (@($found[0].Namespace)-join ',') -ne $created.Namespace)){$errors.Add('An owned NRPT rule was edited outside DevProxy.');$blocked+=@([string]$created.Name)}
    }
    foreach($rule in @($rules | Where-Object {$_.Comment -eq $State.Tag})){
        if([string]$rule.Name -in $blocked){continue}
        try {
            if((@($rule.NameServers | ForEach-Object {[string]$_}) -join ',') -ne [string]$State.Server -or @($rule.Namespace | Where-Object {$_ -notin $State.NewNamespaces}).Count){throw 'An owned NRPT rule was changed outside DevProxy.'}
            Remove-DnsClientNrptRule -Name $rule.Name -Force -ErrorAction Stop
        }catch{$errors.Add($_.Exception.Message)}
    }
    foreach($original in @($State.OriginalRules)){
        if($original.OriginalName -notin @($State.RemovedRuleNames) -and $State.PendingRemove -ne $original.OriginalName){continue}
        try {
            $current=@(Get-DnsClientNrptRule)
            $present=@($current | Where-Object {$_.Name -eq $original.OriginalName -or ($original.RestoredName -and $_.Name -eq $original.RestoredName)})
            if($present.Count){
                if((Rule-Fingerprint (Snapshot-Rule $present[0])) -ne (Rule-Fingerprint $original)){throw 'A previous NRPT rule was edited outside DevProxy.'}
                continue
            }
            if(@($current | Where-Object { $names=$_.Namespace; @($original.Namespace | Where-Object {$_ -in $names}).Count }).Count){throw 'A foreign NRPT rule appeared during rollback.'}
            $restored=Restore-Rule $original;$original.RestoredName=[string]$restored.Name;Save-State $State
        }catch{$errors.Add($_.Exception.Message)}
    }
    Clear-DnsClientCache -ErrorAction Stop
    $State.Errors=@($errors.ToArray());$State.Status=if($errors.Count){'RollbackFailed'}else{'Restored'};Save-State $State
    if($errors.Count){throw ($errors.ToArray()-join '; ')}
}
function Invoke-Apply($Request) {
    $existing=Read-State
    if($existing -and $existing.Status -ne 'Restored'){throw 'A DNS backup is already active. Restore it before applying a new setup.'}
    $state=New-Plan $Request
    if($existing){Write-Json (Join-Path (Get-StoreRoot) ('history-'+$existing.Id+'.json')) $existing}
    Save-State $state
    try {
        $state.Status='Applying';Save-State $state
        foreach($rule in @($state.OriginalRules)){
            $current=@(Get-DnsClientNrptRule | Where-Object {$_.Name -eq $rule.OriginalName})
            if($current.Count -ne 1 -or (Rule-Fingerprint (Snapshot-Rule $current[0])) -ne (Rule-Fingerprint $rule)){throw 'An NRPT rule changed during setup. It was not overwritten.'}
            $state.PendingRemove=$rule.OriginalName;Save-State $state
            Remove-DnsClientNrptRule -Name $rule.OriginalName -Force -ErrorAction Stop
            $state.RemovedRuleNames+=@($rule.OriginalName);$state.PendingRemove=$null;Save-State $state
        }
        foreach($domain in @($state.NewNamespaces)){
            $created=Add-DnsClientNrptRule -Namespace $domain -NameServers @($state.Server) -DisplayName 'DevProxy SmartDNS' -Comment $state.Tag -PassThru -ErrorAction Stop
            $state.CreatedRules+=@([pscustomobject]@{Name=[string]$created.Name;Namespace=$domain});Save-State $state
        }
        foreach($adapter in @($state.OriginalAdapters)){
            $current=Find-Adapter $adapter.Guid
            $state.PendingAdapter=$adapter.Guid;Save-State $state
            Set-DnsClientServerAddress -InterfaceIndex $current.InterfaceIndex -ServerAddresses @($state.Server) -ErrorAction Stop
            $state.AppliedAdapters+=@(Read-Adapter $current);$state.PendingAdapter=$null;Save-State $state
            $servers=@(Get-DnsClientServerAddress -InterfaceIndex $current.InterfaceIndex | ForEach-Object {$_.ServerAddresses} | Where-Object {$_} | Select-Object -Unique)
            if($servers.Count -ne 1 -or $state.Server -notin $servers){throw ('DNS verification failed for '+$adapter.Alias)}
        }
        foreach($domain in @($state.NewNamespaces)){
            if(-not @(Get-DnsClientNrptRule | Where-Object {$_.Comment -eq $state.Tag -and $_.Namespace -contains $domain}).Count){throw 'NRPT verification failed.'}
        }
        Clear-DnsClientCache -ErrorAction Stop
        $state.Status='Applied';Save-State $state
        return ('DNS applied. Adapters: '+@($state.AppliedAdapters).Count+'; new NRPT rules: '+@($state.NewNamespaces).Count+'; reused rules: '+$state.ReusedRules+'. Backup saved.')
    }catch{
        $problem=$_.Exception.Message
        try{Undo-State $state $true}catch{throw ('Apply failed: '+$problem+'. Rollback incomplete: '+$_.Exception.Message)}
        throw ('Apply failed: '+$problem+'. Previous settings restored.')
    }
}
function Invoke-SystemDns($Request) {
    Validate-Request $Request;Assert-Administrator;Initialize-Store
    $mutex=New-Object Threading.Mutex($false,'Global\DevProxy.SystemDns.Operation')
    $owns=$false
    try{
        try{$owns=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owns=$true}
        if(-not $owns){throw 'Another system DNS operation is still running.'}
        if($Request.Operation -eq 'Apply'){return Invoke-Apply $Request}
        $state=Read-State
        if(-not $state){throw 'There is no DevProxy DNS backup.'}
        if($state.Status -eq 'Restored'){return 'Previous DNS settings are already restored.'}
        Undo-State $state
        'Previous DNS settings restored, including DHCP/static mode. DNS cache cleared.'
    }finally{if($owns){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
if($LibraryOnly){return}
if($Operation -eq 'Status'){
    $state=Read-State
    if($state){[pscustomobject]@{Status=$state.Status;Server=$state.Server;Preset=$state.Preset;Adapters=@($state.OriginalAdapters).Count;Rules=@($state.NewNamespaces).Count;Errors=$state.Errors}|ConvertTo-Json -Depth 4}
    else{'{"Status":"NotConfigured"}'}
    exit 0
}
$request=$null
try {
    $request=$RequestJson | ConvertFrom-Json;Validate-Request $request
    if($request.Operation -ne $Operation){throw 'DNS operation mismatch.'}
    $message=Invoke-SystemDns $request
    Write-Json (Join-Path (Get-StoreRoot) ('jobs\'+$request.JobId+'.json')) ([pscustomobject]@{Success=$true;Message=$message})
    exit 0
}catch{
    $message=$_.Exception.Message
    if($request -and [string]$request.JobId -match '^[0-9a-f]{32}$'){
        try{Initialize-Store;Write-Json (Join-Path (Get-StoreRoot) ('jobs\'+$request.JobId+'.json')) ([pscustomobject]@{Success=$false;Message=$message})}catch{}
    }
    Write-Error $message;exit 1
}
