param([ValidateSet('Apply','Restore','ResetAll','Status')][string]$Operation='Status', [string]$RequestJson='{}', [string]$RequestBase64='', [switch]$LibraryOnly)
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
function Read-SharedText([string]$Path) {
    $stream=$null;$reader=$null
    try{
        $share=[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $stream=New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,$share)
        $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true)
        $reader.ReadToEnd()
    }finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
}
function Test-FileSharingViolation($Exception) {
    while($Exception){
        if($Exception -is [IO.IOException] -and ($Exception.HResult -band 0xffff) -in @(32,33)){return $true}
        $Exception=$Exception.InnerException
    }
    return $false
}
function Publish-JsonFile([string]$Temporary,[string]$Path) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($true){try{
        if([IO.File]::Exists($Path)){[IO.File]::Replace($Temporary,$Path,[NullString]::Value)}else{[IO.File]::Move($Temporary,$Path)}
        return
    }catch{
        if(-not (Test-FileSharingViolation $_.Exception)){throw}
        if($clock.ElapsedMilliseconds -ge 3000){throw ('DNS backup file is busy: '+[IO.Path]::GetFileName($Path)+'. Close other DNS windows or wait for the file lock to clear, then retry. The previous backup was not overwritten.')}
        Start-Sleep -Milliseconds 100
    }}
}
function Write-Json([string]$Path,$Value) {
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $json=$Value | ConvertTo-Json -Depth 16
        [IO.File]::WriteAllText($temp,$json,(New-Object Text.UTF8Encoding($false)))
        Protect-Path $temp
        if((Read-SharedText $temp) -ne $json){throw 'DNS backup integrity check failed.'}
        Publish-JsonFile $temp $Path
    }finally{if([IO.File]::Exists($temp)){try{[IO.File]::Delete($temp)}catch{if(-not (Test-FileSharingViolation $_.Exception)){throw}}}}
}
function Read-State {
    $path=Join-Path (Get-StoreRoot) 'state.json'
    if(-not (Test-Path -LiteralPath $path)){return $null}
    if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Invalid backup file.'}
    $owner=(Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner -notin @('S-1-5-32-544','S-1-5-18')){throw 'The DNS backup is not administrator-owned. It will not be used.'}
    Read-SharedText $path | ConvertFrom-Json
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
    if($Request.Operation -notin @('Apply','Restore','ResetAll')){throw 'Invalid DNS operation.'}
    if($Request.Operation -eq 'ResetAll'){if($Request.ConfirmResetAll -ne $true){throw 'Full DNS reset requires explicit confirmation.'};return}
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
function Get-ConnectivityTargets {
    @([pscustomobject]@{HostName='example.com';Uri='https://example.com/'},
      [pscustomobject]@{HostName='www.microsoft.com';Uri='https://www.microsoft.com/'},
      [pscustomobject]@{HostName='www.cloudflare.com';Uri='https://www.cloudflare.com/'})
}
function Test-DnsProbe([string]$HostName,[string]$Server='') {
    try {
        $parameters=@{Name=$HostName;Type='A';DnsOnly=$true;NoHostsFile=$true;QuickTimeout=$true;ErrorAction='Stop'}
        if($Server){$parameters.Server=$Server}
        $answers=@(Resolve-DnsName @parameters | Where-Object {$_.IPAddress})
        return $answers.Count -gt 0
    }catch{return $false}
}
function Test-HttpsProbe([string]$Uri) {
    $response=$null;$request=$null
    $oldDnsRefresh=[Net.ServicePointManager]::DnsRefreshTimeout
    try {
        [Net.ServicePointManager]::DnsRefreshTimeout=0
        $request=[Net.HttpWebRequest]::Create($Uri)
        $request.Method='HEAD';$request.Proxy=$null;$request.AllowAutoRedirect=$false
        $request.KeepAlive=$false;$request.ConnectionGroupName='DevProxyDnsProbe-'+[guid]::NewGuid().ToString('N')
        $request.Timeout=4000;$request.ReadWriteTimeout=4000
        $response=$request.GetResponse()
        return [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 500
    }catch [Net.WebException]{
        $response=$_.Exception.Response
        return $null -ne $response -and [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 500
    }catch{return $false}
    finally{if($response){$response.Close()};if($request){$request.Abort()};[Net.ServicePointManager]::DnsRefreshTimeout=$oldDnsRefresh}
}
function Read-NetworkBaseline {
    $working=@(Get-ConnectivityTargets | Where-Object {(Test-DnsProbe $_.HostName) -and (Test-HttpsProbe $_.Uri)})
    if($working.Count -lt 2){throw 'Connectivity preflight failed: cannot verify working DNS and HTTPS on two independent sites. DNS settings were not changed.'}
    $working
}
function Test-ServerHttpsProbe($Target,[string]$Server) {
    # Pin a TCP/TLS connection to the proposed resolver's answers without
    # changing Windows DNS. SslStream checks both the chain and the host name;
    # no permissive certificate callback is installed. Try IPv4 and IPv6.
    $answers=@()
    foreach($type in @('A','AAAA')){
        try{$answers+=@(Resolve-DnsName -Name $Target.HostName -Type $type -Server $Server -DnsOnly -NoHostsFile -QuickTimeout -ErrorAction Stop | Where-Object {$_.IPAddress} | Select-Object -First 2)}catch{}
    }
    foreach($answer in $answers){
        $ip=$null;$tcp=$null;$tls=$null;$reader=$null
        if(-not [Net.IPAddress]::TryParse([string]$answer.IPAddress,[ref]$ip)){continue}
        try {
            $tcp=[Net.Sockets.TcpClient]::new($ip.AddressFamily)
            if(-not $tcp.ConnectAsync($ip,443).Wait(3000)){continue}
            $tls=[Net.Security.SslStream]::new($tcp.GetStream(),$false)
            if(-not $tls.AuthenticateAsClientAsync($Target.HostName,$null,[Security.Authentication.SslProtocols]::Tls12,$false).Wait(4000)){continue}
            $tls.ReadTimeout=4000;$tls.WriteTimeout=4000
            $bytes=[Text.Encoding]::ASCII.GetBytes("HEAD / HTTP/1.1`r`nHost: "+$Target.HostName+"`r`nConnection: close`r`n`r`n")
            $tls.Write($bytes,0,$bytes.Length)
            $reader=[IO.StreamReader]::new($tls,[Text.Encoding]::ASCII)
            if($reader.ReadLine() -match '^HTTP/1\.[01] [234][0-9][0-9]( |$)'){return $true}
        }catch{}
        finally{if($reader){$reader.Dispose()};if($tls){$tls.Dispose()};if($tcp){$tcp.Close()}}
    }
    return $false
}
function Assert-TargetDns([string]$Server,$Targets) {
    foreach($target in @($Targets)){
        if(-not (Test-DnsProbe $target.HostName $Server)){throw ('DNS server preflight failed: '+$Server+' did not resolve '+$target.HostName+'. DNS settings were not changed.')}
        if(-not (Test-ServerHttpsProbe $target $Server)){throw ('DNS server preflight failed: HTTPS through answers from '+$Server+' is unavailable for '+$target.HostName+'. DNS settings were not changed.')}
    }
}
function Assert-NetworkConnectivity($Targets) {
    foreach($target in @($Targets)){
        if(-not (Test-DnsProbe $target.HostName)){throw ('Connectivity verification failed: system DNS cannot resolve '+$target.HostName)}
        if(-not (Test-HttpsProbe $target.Uri)){throw ('Connectivity verification failed: HTTPS is unavailable for '+$target.HostName)}
    }
}
function Read-SelectedAdapters($Request) {
    $adapters=@()
    if($Request.ChangeAdapters){foreach($selected in @($Request.Adapters)){
        $adapter=Find-Adapter ([string]$selected.Guid)
        if([int]$adapter.InterfaceIndex -ne [int]$selected.Index -or [string]$adapter.Status -ne 'Up'){throw 'Adapter state changed; refresh the adapter list.'}
        $adapters+=Read-Adapter $adapter
    }}
    $adapters
}
function New-Plan($Request) {
    $adapters=@(Read-SelectedAdapters $Request)
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
    $family=if(([Net.IPAddress]::Parse($Request.Server)).AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork){'IPv4'}else{'IPv6'}
    [pscustomobject]@{Version=1;Status='Prepared';Id=[string]$Request.JobId;Server=[string]$Request.Server;ServerFamily=$family;Preset=[string]$Request.Preset;
        ChangeAdapters=[bool]$Request.ChangeAdapters;UseNrpt=[bool]$Request.UseNrpt;ReplaceExisting=[bool]$Request.ReplaceExisting;
        OriginalAdapters=$adapters;AppliedAdapters=@();PendingAdapter=$null;OriginalRules=$replace;
        RemovedRuleNames=@();PendingRemove=$null;NewNamespaces=$add;CreatedRules=@();ReusedRules=$reused;Tag=('DEVPROXY_SMARTDNS_V1:'+([string]$Request.JobId));Errors=@()}
}
function Restore-Adapter($Snapshot) {
    $adapter=Find-Adapter ([string]$Snapshot.Guid)
    foreach($family in @($Snapshot.Families)){
        $dnsInput=Get-DnsClientServerAddress -InterfaceIndex $adapter.InterfaceIndex -AddressFamily $family.Family -ErrorAction Stop
        if($family.Source -eq 'Dhcp'){Set-DnsClientServerAddress -InputObject $dnsInput -ResetServerAddresses -ErrorAction Stop}
        else{Set-DnsClientServerAddress -InputObject $dnsInput -ServerAddresses @($family.Servers) -ErrorAction Stop}
    }
    $actual=Read-Adapter $adapter
    $actual.Families=@($actual.Families | Where-Object {$_.Family -in @($Snapshot.Families.Family)})
    if((Adapter-Fingerprint $actual) -ne (Adapter-Fingerprint $Snapshot)){throw ('DNS restore verification failed for '+$Snapshot.Alias)}
}
function Undo-State($State,[bool]$FailureRollback=$false,[bool]$KeepExternalChanges=$false) {
    $errors=New-Object 'Collections.Generic.List[string]'
    $warnings=New-Object 'Collections.Generic.List[string]'
    foreach($original in @($State.OriginalAdapters)){
        $applied=@($State.AppliedAdapters | Where-Object {$_.Guid -eq $original.Guid})
        if(-not $applied.Count -and $State.PendingAdapter -ne $original.Guid){continue}
        try {
            # A missing device is only released when the user explicitly asks to
            # keep external changes. Keep its snapshot for later manual recovery.
            $devices=@(Get-NetAdapter -IncludeHidden | Where-Object {([guid]$_.InterfaceGuid).ToString() -eq ([guid]$original.Guid).ToString()})
            if(-not $devices.Count -and $KeepExternalChanges){$warnings.Add('Adapter is missing; backup retained: '+$original.Alias);continue}
            $current=Read-Adapter (Find-Adapter $original.Guid)
            if((Adapter-Fingerprint $current) -eq (Adapter-Fingerprint $original)){continue}
            foreach($family in @($original.Families)){
                $now=@($current.Families | Where-Object Family -eq $family.Family)[0]
                $before=[pscustomobject]@{Families=@($family)}
                if((Adapter-Fingerprint ([pscustomobject]@{Families=@($now)})) -eq (Adapter-Fingerprint $before)){continue}
                $expected=@(if($applied.Count){$applied[0].Families | Where-Object Family -eq $family.Family})
                if(-not $FailureRollback -and $expected.Count -and
                    (Adapter-Fingerprint ([pscustomobject]@{Families=@($now)})) -ne (Adapter-Fingerprint ([pscustomobject]@{Families=@($expected[0])}))){
                    $message='DNS changed outside DevProxy: '+$original.Alias+' ('+$family.Family+')'
                    if($KeepExternalChanges){$warnings.Add($message);continue}
                    $errors.Add($message);continue
                }
                # Restore only this address family; an external IPv6 edit must
                # not stop restoration of IPv4 still owned by this operation.
                Restore-Adapter ([pscustomobject]@{Guid=$original.Guid;Alias=$original.Alias;Families=@($family)})
            }
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
            # Several baseline rules may cover the same namespace. Rules we
            # already restored (including before a retry) are not foreign.
            $overlaps=@($current | Where-Object { $names=$_.Namespace; @($original.Namespace | Where-Object {$_ -in $names}).Count })
            $foreign=@($overlaps | Where-Object {
                $candidate=$_
                -not @($State.OriginalRules | Where-Object {
                    ($_.OriginalName -eq $candidate.Name -or ($_.RestoredName -and $_.RestoredName -eq $candidate.Name)) -and
                    (Rule-Fingerprint (Snapshot-Rule $candidate)) -eq (Rule-Fingerprint $_)
                }).Count
            })
            if($foreign.Count){
                $message='A foreign NRPT rule appeared during rollback: '+(@($original.Namespace)-join ', ')
                if($KeepExternalChanges){$warnings.Add($message);continue}
                throw $message
            }
            $restored=Restore-Rule $original;$original.RestoredName=[string]$restored.Name;Save-State $State
        }catch{$errors.Add($_.Exception.Message)}
    }
    Clear-DnsClientCache -ErrorAction Stop
    $State | Add-Member -NotePropertyName Warnings -NotePropertyValue @($warnings.ToArray()) -Force
    $State.Errors=@($errors.ToArray());$State.Status=if($errors.Count){'RollbackFailed'}elseif($warnings.Count){'Released'}else{'Restored'};Save-State $State
    if($errors.Count){throw ($errors.ToArray()-join '; ')}
}
function Get-ResetNrptStores {
    @('HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig',
      'HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig')
}
function Backup-ResetNrptStores([string]$Directory) {
    $number=0
    foreach($key in @(Get-ResetNrptStores)){
        $file=Join-Path $Directory ('nrpt-'+$number+'.reg');$number++
        $exists=Test-Path -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\'+$key.Substring(5))
        if($exists){
            $null=& "$env:SystemRoot\System32\reg.exe" export $key $file /y 2>&1
            if($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -lt 20){throw 'NRPT backup failed; no DNS settings changed.'}
            Protect-Path $file
        }
        [pscustomobject]@{Key=$key;File=[IO.Path]::GetFileName($file);Exists=[bool]$exists}
    }
}
function Clear-ResetNrptStores {
    foreach($key in @(Get-ResetNrptStores)){
        $path='Registry::HKEY_LOCAL_MACHINE\'+$key.Substring(5)
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop}
    }
}
function Test-ResetNrptStoresEmpty {
    foreach($key in @(Get-ResetNrptStores)){if(Test-Path -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\'+$key.Substring(5))){return $false}}
    return $true
}
function Restore-ResetNrptStores($State) {
    $allowed=@(Get-ResetNrptStores)
    foreach($store in @($State.RegistryStores)){
        if($store.Key -notin $allowed -or $store.File -notmatch '^nrpt-[01]\.reg$'){throw 'Invalid NRPT reset backup.'}
        if(-not $store.Exists){continue}
        $file=Join-Path $State.ResetDirectory $store.File
        if(-not (Test-Path -LiteralPath $file) -or ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Invalid NRPT reset backup file.'}
        $null=& "$env:SystemRoot\System32\reg.exe" import $file 2>&1
        if($LASTEXITCODE -ne 0){throw 'NRPT backup import failed.'}
    }
}
function Invoke-ResetAll($Request) {
    # No connectivity preflight: this is also the escape path for broken DNS.
    # Read and persist ALL originals before the first network mutation.
    $existing=Read-State
    $directory=Join-Path (Get-StoreRoot) ('reset-'+$Request.JobId)
    [IO.Directory]::CreateDirectory($directory)|Out-Null;Protect-Path $directory $true
    $warnings=New-Object 'Collections.Generic.List[string]'
    # Enumerate once: filtering CIM by a phantom InterfaceIndex throws instead
    # of returning an empty result (e.g. disconnected virtual adapter 37).
    $dnsRows=@(Get-DnsClientServerAddress -ErrorAction Stop)
    $adapters=@(foreach($adapter in @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)){
        $families=@($dnsRows | Where-Object {$_.InterfaceIndex -eq $adapter.InterfaceIndex} | ForEach-Object {
            # AddressFamily is 2/23 on the native CIM object; the fixture has Family.
            if($_.AddressFamily -eq 2 -or $_.Family -eq 'IPv4'){'IPv4'}elseif($_.AddressFamily -eq 23 -or $_.Family -eq 'IPv6'){'IPv6'}
        } | Select-Object -Unique)
        if($families.Count){$snapshot=Read-Adapter $adapter;$snapshot.Families=@($snapshot.Families | Where-Object {$_.Family -in $families});$snapshot}
        else{$warnings.Add('Skipped adapter without DNS bindings: '+$adapter.Name+' ['+$adapter.InterfaceIndex+']')}
    })
    $rules=@(Get-DnsClientNrptRule -ErrorAction Stop)
    # Raw registry exports retain DNSSEC/DirectAccess and all unknown rule fields.
    $stores=@(Backup-ResetNrptStores $directory)
    $state=[pscustomobject]@{Version=1;Kind='ResetAll';Status='Clearing';Id=$Request.JobId;Server='';Preset='';ResetDirectory=$directory;
        OriginalAdapters=$adapters;OriginalRules=@($rules | Select-Object *);RegistryStores=$stores;NrptRestored=$false;Warnings=@($warnings.ToArray());Errors=@()}
    Write-Json (Join-Path $directory 'snapshot.json') $state
    if($existing){Write-Json (Join-Path (Get-StoreRoot) ('history-'+$existing.Id+'.json')) $existing}
    Save-State $state
    $errors=New-Object 'Collections.Generic.List[string]'
    foreach($original in $adapters){foreach($family in $original.Families){try{
        $matches=@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {([guid]$_.InterfaceGuid).ToString() -eq $original.Guid})
        if(-not $matches.Count){$warnings.Add('Adapter disappeared during reset: '+$original.Alias);continue}
        $adapter=$matches[0]
        $resetFamilyName=[string]$family.Family
        $currentDnsRows=@(Get-DnsClientServerAddress -ErrorAction Stop)
        $dnsInput=@($currentDnsRows | Where-Object {
            $rowFamily=if($_.AddressFamily -eq 2 -or $_.Family -eq 'IPv4'){'IPv4'}elseif($_.AddressFamily -eq 23 -or $_.Family -eq 'IPv6'){'IPv6'}else{''}
            $_.InterfaceIndex -eq $adapter.InterfaceIndex -and $rowFamily -eq $resetFamilyName
        })
        if(-not $dnsInput.Count){$warnings.Add('DNS binding disappeared during reset: '+$original.Alias+' ('+$family.Family+')');continue}
        Set-DnsClientServerAddress -InputObject $dnsInput[0] -ResetServerAddresses -ErrorAction Stop
        if((Read-Family $adapter $family.Family).Source -ne 'Dhcp'){throw ('DNS reset verification failed: '+$original.Alias+' ('+$family.Family+')')}
    }catch{$errors.Add($_.Exception.Message)}}}
    foreach($rule in $rules){try{Remove-DnsClientNrptRule -Name $rule.Name -Force -ErrorAction Stop}catch{$errors.Add($_.Exception.Message)}}
    try{Clear-ResetNrptStores}catch{$errors.Add($_.Exception.Message)}
    try{Clear-DnsClientCache -ErrorAction Stop}catch{$errors.Add($_.Exception.Message)}
    try{
        if(@(Get-DnsClientNrptRule -ErrorAction Stop).Count -or -not (Test-ResetNrptStoresEmpty) -or @(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop).Count){throw 'NRPT rules remain or were reapplied by policy.'}
    }catch{$errors.Add($_.Exception.Message)}
    $state.Errors=@($errors.ToArray());$state.Warnings=@($warnings.ToArray());$state.Status=if($errors.Count){'ClearFailed'}else{'Cleared'};Save-State $state
    if($errors.Count){throw ('Full DNS reset incomplete: '+($errors.ToArray()-join '; ')+'. Backup: '+$directory)}
    return ('Full DNS reset completed. Adapters: '+$adapters.Count+'; removed NRPT rules: '+$rules.Count+'. Backup: '+$directory+'. Details: '+($warnings.ToArray()-join '; '))
}
function Undo-ResetAll($State,[bool]$KeepExternalChanges) {
    $expected=Join-Path (Get-StoreRoot) ('reset-'+$State.Id)
    if($State.Id -notmatch '^[0-9a-f]{32}$' -or $State.ResetDirectory -ne $expected){throw 'Invalid DNS reset backup path.'}
    $errors=New-Object 'Collections.Generic.List[string]';$warnings=New-Object 'Collections.Generic.List[string]'
    foreach($original in @($State.OriginalAdapters)){try{
        $adapter=Find-Adapter $original.Guid
        foreach($family in @($original.Families)){
            $now=Read-Family $adapter $family.Family
            if((Adapter-Fingerprint ([pscustomobject]@{Families=@($now)})) -eq (Adapter-Fingerprint ([pscustomobject]@{Families=@($family)}))){continue}
            if($now.Source -ne 'Dhcp' -and $KeepExternalChanges){$warnings.Add('External DNS retained: '+$original.Alias+' ('+$family.Family+')');continue}
            Restore-Adapter ([pscustomobject]@{Guid=$original.Guid;Alias=$original.Alias;Families=@($family)})
        }
    }catch{if($KeepExternalChanges -and $_.Exception.Message -like 'Adapter is missing:*'){$warnings.Add($_.Exception.Message)}else{$errors.Add($_.Exception.Message)}}}
    if(-not $State.NrptRestored){try{
        if(@(Get-DnsClientNrptRule -ErrorAction Stop).Count -or -not (Test-ResetNrptStoresEmpty)){throw 'New NRPT rules exist after reset; backup retained. Restore them manually to avoid overwriting external settings.'}
        Restore-ResetNrptStores $State;$State.NrptRestored=$true;Save-State $State
    }catch{$errors.Add($_.Exception.Message)}}
    try{Clear-DnsClientCache -ErrorAction Stop}catch{$errors.Add($_.Exception.Message)}
    $State.Errors=@($errors.ToArray());$State.Warnings=@($warnings.ToArray());$State.Status=if($errors.Count){'RollbackFailed'}elseif($warnings.Count){'Released'}else{'Restored'};Save-State $State
    if($errors.Count){throw ($errors.ToArray()-join '; ')}
    return ('Pre-reset DNS settings restored. Backup retained. Details: '+($warnings.ToArray()-join '; '))
}
function Invoke-Apply($Request) {
    $existing=Read-State
    $previousWarnings=@()
    if($existing -and $existing.Kind -ne 'ResetAll' -and $existing.Status -notin @('Restored','Released') -and -not $Request.ReplaceActiveSetup){throw 'A DNS backup is already active. Restore it before applying a new setup.'}
    $null=Read-SelectedAdapters $Request
    # Network probes run before any DNS/NRPT change, even before undoing an
    # existing setup. A server that cannot answer ordinary queries is refused.
    $baseline=@(Read-NetworkBaseline)
    Assert-TargetDns $Request.Server $baseline
    if($existing -and $existing.Kind -ne 'ResetAll' -and $existing.Status -notin @('Restored','Released')){
        if(-not $Request.ReplaceActiveSetup){throw 'A DNS backup is already active. Restore it before applying a new setup.'}
        # Reject a stale adapter selection before touching the active setup.
        $null=Read-SelectedAdapters $Request
        Undo-State $existing $false ([bool]$Request.KeepExternalChanges)
    }
    if($existing){$previousWarnings=@($existing.Warnings | Where-Object {$_})}
    $state=New-Plan $Request
    $state | Add-Member -NotePropertyName PreviousWarnings -NotePropertyValue $previousWarnings
    $state | Add-Member -NotePropertyName ConnectivityTargets -NotePropertyValue $baseline
    $state | Add-Member -NotePropertyName ConnectivityVerified -NotePropertyValue $false
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
            $dnsInput=@(Get-DnsClientServerAddress -InterfaceIndex $current.InterfaceIndex -AddressFamily $state.ServerFamily -ErrorAction Stop)
            if($dnsInput.Count -ne 1){throw ('DNS address family is unavailable for '+$adapter.Alias+': '+$state.ServerFamily)}
            $state.PendingAdapter=$adapter.Guid;Save-State $state
            Set-DnsClientServerAddress -InputObject $dnsInput[0] -ServerAddresses @($state.Server) -ErrorAction Stop
            $state.AppliedAdapters+=@(Read-Adapter $current);$state.PendingAdapter=$null;Save-State $state
            $servers=@(Get-DnsClientServerAddress -InterfaceIndex $current.InterfaceIndex -AddressFamily $state.ServerFamily -ErrorAction Stop | ForEach-Object {$_.ServerAddresses} | Where-Object {$_} | Select-Object -Unique)
            if($servers.Count -ne 1 -or $state.Server -notin $servers){throw ('DNS verification failed for '+$adapter.Alias+' ('+$state.ServerFamily+'). Actual servers: '+($servers -join ', '))}
        }
        foreach($domain in @($state.NewNamespaces)){
            if(-not @(Get-DnsClientNrptRule | Where-Object {$_.Comment -eq $state.Tag -and $_.Namespace -contains $domain}).Count){throw 'NRPT verification failed.'}
        }
        Clear-DnsClientCache -ErrorAction Stop
        Assert-NetworkConnectivity $baseline
        $state.ConnectivityVerified=$true
        $state.Status='Applied';Save-State $state
        $message='DNS applied. Family: '+$state.ServerFamily+'. DNS and HTTPS checks passed. Adapters: '+@($state.AppliedAdapters).Count+'; new NRPT rules: '+@($state.NewNamespaces).Count+'; reused rules: '+$state.ReusedRules+'. Backup saved.'
        if($previousWarnings.Count){$message+=' Previous setup closed with preserved external settings: '+($previousWarnings -join '; ')}
        return $message
    }catch{
        $problem=$_.Exception.Message
        try{Undo-State $state $true}catch{throw ('Apply failed: '+$problem+'. Rollback incomplete: '+$_.Exception.Message)}
        try{Assert-NetworkConnectivity $baseline}catch{
            $state | Add-Member -NotePropertyName RecoveryConnectivityVerified -NotePropertyValue $false -Force;Save-State $state
            throw ('Apply failed: '+$problem+'. Previous settings restored, but connectivity could not be verified: '+$_.Exception.Message)
        }
        $state | Add-Member -NotePropertyName RecoveryConnectivityVerified -NotePropertyValue $true -Force;Save-State $state
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
        if($Request.Operation -eq 'ResetAll'){return Invoke-ResetAll $Request}
        $state=Read-State
        if(-not $state){throw 'There is no DevProxy DNS backup.'}
        if($state.Status -eq 'Restored'){return 'Previous DNS settings are already restored.'}
        if($state.Status -eq 'Released'){return ('DNS rollback completed with preserved external settings. Backup retained. Details: '+(@($state.Warnings)-join '; '))}
        if($state.Kind -eq 'ResetAll'){return Undo-ResetAll $state ([bool]$Request.KeepExternalChanges)}
        Undo-State $state $false ([bool]$Request.KeepExternalChanges)
        if($state.Status -eq 'Released'){return ('DNS rollback completed with preserved external settings. Backup retained. Details: '+(@($state.Warnings)-join '; '))}
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
