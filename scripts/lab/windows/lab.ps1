#requires -Version 7.2
<#
.SYNOPSIS
Client of the remote Wino mail lab: leases accounts, installs them into the Debug app, and drives the lab's control
API to change, inspect and trace mailboxes. Run `lab.ps1 help` for the commands; tools/lab/README.md is the guide.
.EXAMPLE
.\scripts\lab\windows\lab.ps1 status
.EXAMPLE
.\scripts\lab\windows\lab.ps1 install-app
.EXAMPLE
.\scripts\lab\windows\lab.ps1 act alice1@dovecot.test deliver -Subject 'Hello' -Count 3
.EXAMPLE
.\scripts\lab\windows\lab.ps1 inspect alice1@dovecot.test -Folder INBOX
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'doctor', 'manifest', 'lease', 'leases', 'renew', 'release', 'generate-db', 'install-app',
        'reset', 'inspect', 'act', 'import', 'corpus', 'fault', 'trace', 'ca', 'tunnel', 'help')]
    [string]$Command = 'help',

    # The account address (reset, inspect, act, import, trace), corpus message, fault target, or lease id.
    [Parameter(Position = 1)][string]$Target,
    # The action type (act), message file (import), or fault kind (fault).
    [Parameter(Position = 2)][string]$Argument,
    # fault: the fault parameters as JSON or key=value pairs, for example latency=2000,jitter=200.
    [Parameter(Position = 3)][string]$Parameters,

    [string[]]$Servers,
    [switch]$Pop3,
    [string]$Holder,
    [int]$TtlHours = 24,
    [string]$Id,
    [switch]$All,
    [string]$Lease,
    [switch]$Shared,
    [switch]$LegacyPlaintext,
    [switch]$Force,

    [string]$Folder,
    [string[]]$Uid,
    [string[]]$MessageId,
    [int]$Limit = 50,
    [long]$SinceUid,
    [switch]$Calendars,
    [string]$Calendar,
    [switch]$Contacts,
    [string]$AddressBook,
    [switch]$Json,
    [string]$Out,

    [string]$Body,
    [string]$To,
    [string[]]$Add,
    [string[]]$Remove,
    [string]$From,
    [string]$Subject,
    [string]$Text,
    [string]$Html,
    [int]$Count,
    [string[]]$Attach,
    [string]$Corpus,
    [switch]$Expunge,
    [string]$Method,
    [string]$Style,
    [string]$Summary,
    [string]$Start,
    [string]$End,
    [string]$Recurrence,
    [string[]]$Flags,

    [int]$Minutes = 15,
    [switch]$Clear,
    [string]$Since = '10m',
    [string]$Protocol
)
$ErrorActionPreference = 'Stop'
$boundParameters = $PSBoundParameters
Import-Module (Join-Path $PSScriptRoot 'LabClient.psm1') -Force
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$labRoot = Join-Path $repositoryRoot 'tools/lab'
$stateRoot = Get-LabStateRoot
$manifestPath = Get-LabManifestPath
$generatedRoot = Join-Path $stateRoot 'generated'
$labHost = Get-LabHostName
$sshHost = if ($env:WINO_LAB_SSH_HOST) { $env:WINO_LAB_SSH_HOST } else { 'wino-lab' }
# The lab's plaintext loopback ports, forwarded one to one by `tunnel`.
$tunnelPorts = @(1143, 1587, 4025, 2143, 2587, 3143, 3587, 8803, 4143, 5143, 5587, 8800, 8802, 8804, 8805,
    1993, 2993, 3993, 4993, 1465, 2465, 3465, 4465, 8443, 8444, 8445, 8446, 8807)

function Split-List([string[]]$Values) {
    @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Write-Table($Rows, [object[]]$Properties) {
    $Rows = @($Rows | Where-Object { $null -ne $_ })
    if (!$Rows.Count) { Write-Output '(none)'; return }
    $Rows | Format-Table -Property $Properties -AutoSize | Out-String -Width 220 | ForEach-Object { Protect-LabText $_.TrimEnd() }
}

function Write-Result($Value) {
    if ($Json) { Protect-LabText (ConvertTo-Json -InputObject $Value -Depth 20) } else { $Value }
}

function Read-JsonArgument([string]$Value) {
    if (!$Value) { return [ordered]@{} }
    $source = if ($Value.StartsWith('@')) { Get-Content -LiteralPath $Value.Substring(1) -Raw } else { $Value }
    $source | ConvertFrom-Json -AsHashtable
}

function ConvertFrom-ParameterText([string]$Value) {
    if (!$Value) { return [ordered]@{} }
    if ($Value.TrimStart().StartsWith('{') -or $Value.StartsWith('@')) { return Read-JsonArgument $Value }
    $result = [ordered]@{}
    foreach ($pair in $Value -split '[,;]') {
        $name, $text = $pair -split '=', 2
        if (!$name.Trim() -or $null -eq $text) { throw "Fault parameters are JSON or name=value pairs, not '$pair'." }
        $integer = 0L
        $number = 0.0
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        $result[$name.Trim()] = if ([long]::TryParse($text, [Globalization.NumberStyles]::Integer, $invariant, [ref]$integer)) { $integer }
        elseif ([double]::TryParse($text, [Globalization.NumberStyles]::Float, $invariant, [ref]$number)) { $number }
        else { $text.Trim() }
    }
    $result
}

function Get-RequiredTarget([string]$What) {
    if (!$Target) { throw "Name $What, for example: lab.ps1 $Command alice1@dovecot.test" }
    $Target
}

function Test-Endpoints {
    $checks = [Collections.Generic.List[object]]::new()
    $hosts = [ordered]@{ "control.$labHost" = @(443) }
    foreach ($server in 'dovecot', 'condstore', 'stalwart', 'james', 'cyrus', 'oauth', 'flaky') { $hosts["$server.$labHost"] = @(993, 465, 443) }
    $hosts["greenmail.$labHost"] = @(993, 443)
    $hosts["pop3.$labHost"] = @(995, 993, 465)
    foreach ($server in 'cert-valid', 'cert-wronghost', 'cert-expired', 'cert-revoked') { $hosts["$server.$labHost"] = @(993) }
    $manifest = Get-LabManifest -Optional
    if ($manifest -and $manifest.PSObject.Properties['discoveryAccounts']) {
        foreach ($account in $manifest.discoveryAccounts) { if ($account.imapHost) { $hosts[$account.imapHost] = @(993, 465) } }
    }
    foreach ($name in $hosts.Keys) {
        foreach ($port in $hosts[$name]) {
            $client = [Net.Sockets.TcpClient]::new()
            $checks.Add([pscustomobject]@{ Host = $name; Port = $port; Client = $client; Task = $client.ConnectAsync($name, $port) })
        }
    }
    try { [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($checks.Task), 5000) | Out-Null } catch { }
    foreach ($group in $checks | Group-Object Host) {
        $ports = foreach ($check in $group.Group) {
            "$($check.Port) $(if ($check.Task.IsCompletedSuccessfully) { 'ok' } else { 'FAIL' })"
            $check.Client.Dispose()
        }
        '  {0,-34} {1}' -f $group.Name, ($ports -join '  ')
    }
}

function Show-Status {
    Write-Output "Endpoints ($labHost):"
    Test-Endpoints

    $manifest = Get-LabManifest -Optional
    if (!$manifest) {
        Write-Output "Manifest: $manifestPath does not exist. This machine is not set up for the lab; the owner sets it up with lab.ps1 manifest."
        return
    }
    $schema = Get-LabValue $manifest 'schema'
    Write-Output "Manifest: $manifestPath (schema $(if ($schema) { $schema } else { 1 }), $(@($manifest.accounts).Count) shared accounts, $(@(Get-LabValue $manifest 'pop3Accounts').Count) POP3)"
    $issue = Get-LabManifestIssue $manifest
    if ($issue) { Write-Output "  $issue" }

    try {
        $ping = Invoke-LabApi -Path '/ping' -Anonymous
        Write-Output "Control API: $(Get-LabControlUrl) ping ok=$(Get-LabValue $ping 'ok')"
    } catch {
        Write-Output "Control API: $($_.Exception.Message)"
        return
    }
    if ($issue) { return }
    try {
        $health = Invoke-LabApi -Path '/health'
        Write-Output "Health: ok=$($health.ok) version=$(Get-LabValue $health 'version') leases=$(Get-LabValue $health 'leases') faults=$(@(Get-LabValue $health 'faults').Count)"
        foreach ($server in @(Get-LabValue $health 'servers')) {
            $failed = @(Get-LabValue $server 'checks' | Where-Object { !$_.ok })
            $detail = if ($failed.Count) { ' failing: ' + (($failed | ForEach-Object { "$($_.name) ($(Get-LabValue $_ 'detail'))" }) -join '; ') } else { '' }
            Write-Output (Protect-LabText ('  {0,-12} {1}{2}' -f $server.key, $(if ($server.ok) { 'ok' } else { 'FAIL' }), $detail))
        }
    } catch { Write-Output "Health: $($_.Exception.Message)" }

    $local = @(Get-LabLocalLease)
    Write-Output "Local leases: $($local.Count)"
    foreach ($item in $local) { Write-Output ('  {0}  {1}  expires {2}{3}' -f $item.id, $item.holder, (Format-LabDate $item.expiresAt), $(if (Test-LabLeaseValid $item ([TimeSpan]::Zero)) { '' } else { ' (expired)' })) }
}

function Get-Manifest {
    # The manifest carries the lab password and the control token, so it stays outside the repository and is never printed.
    if (!(Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'OpenSSH client (ssh) is required to fetch the manifest.' }
    $json = & ssh $sshHost 'wino-lab/lab.sh manifest'
    if ($LASTEXITCODE -ne 0 -or !$json) { throw "Could not read the manifest from '$sshHost'. Check the SSH alias (WINO_LAB_SSH_HOST) and that the lab is up." }
    $parsed = ($json -join "`n") | ConvertFrom-Json
    if (!(Get-LabValue $parsed 'accounts') -or !(Get-LabValue $parsed 'password')) { throw 'The lab returned a manifest without accounts or password.' }
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    Set-Content -LiteralPath $manifestPath -Value ($json -join "`n") -Encoding utf8NoBOM
    $manifest = Get-LabManifest -Reload
    Write-Output "Manifest saved to $manifestPath (schema $(if (Get-LabValue $manifest 'schema') { $manifest.schema } else { 1 }), $(@($manifest.accounts).Count) shared accounts)."
    $issue = Get-LabManifestIssue $manifest
    if ($issue) { Write-Warning 'The lab host returned a manifest without control API settings; lease, inspect, act and the other API commands need a schema 2 manifest.' }
}

function Get-HolderName {
    if ($Holder) { $Holder } else { Get-LabDefaultHolder (Split-Path $repositoryRoot -Leaf) }
}

function Find-HolderLease([string]$Name) {
    @(Get-LabLocalLease | Where-Object { $_.holder -eq $Name } | Sort-Object { ConvertTo-LabDate $_.expiresAt } -Descending)
}

function Resolve-LeaseId {
    $value = if ($Id) { $Id } elseif ($Lease) { $Lease } else { $Target }
    if ($value) { return $value }
    $owned = @(Find-HolderLease (Get-HolderName))
    if (!$owned.Count) { throw "No local lease for $(Get-HolderName). Pass -Id (lab.ps1 leases lists them)." }
    $owned[0].id
}

function New-Lease {
    $lease = New-LabLease -Holder (Get-HolderName) -Servers (Split-List $Servers) -Pop3:$Pop3 -TtlHours $TtlHours
    Format-LabLease $lease
}

function Show-Leases {
    $remote = @(Invoke-LabApi -Path '/leases')
    $local = @(Get-LabLocalLease)
    foreach ($stale in $local | Where-Object { $_.id -notin $remote.id }) {
        Remove-Item -LiteralPath $stale.path -Force
        Write-Output "Removed the local file of lease $($stale.id); the lab no longer has it."
    }
    $rows = foreach ($item in $remote) {
        [pscustomobject]@{
            Id = $item.id
            Holder = $item.holder
            Expires = Format-LabDate $item.expiresAt
            Local = if ($item.id -in $local.id) { 'yes' } else { '' }
            Accounts = (@(Get-LabValue $item 'accounts') + @(Get-LabValue $item 'pop3Accounts') | Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.address } }) -join ', '
        }
    }
    Write-Table $rows Id, Holder, Expires, Local, Accounts
}

function Update-Lease {
    $leaseId = Resolve-LeaseId
    $item = @(Get-LabLocalLease | Where-Object { $_.id -eq $leaseId })[0]
    if (!$item) {
        $renewed = Invoke-LabApi -Method POST -Path "/leases/$([Uri]::EscapeDataString($leaseId))/renew" -Body @{ ttlHours = $TtlHours }
        Write-Output "Lease $leaseId renewed until $(Format-LabDate $renewed.expiresAt) (no local file)."
        return
    }
    $item = Update-LabLease $item $TtlHours
    Write-Output "Lease $leaseId renewed until $(Format-LabDate $item.expiresAt)."
}

function Remove-Lease {
    $ids = if ($All) { @(Get-LabLocalLease).id } else { @(Resolve-LeaseId) }
    if (!$ids) { Write-Output 'No local leases.'; return }
    foreach ($leaseId in $ids) {
        Remove-LabLease $leaseId
        Write-Output "Released lease $leaseId."
    }
}

function Get-LeaseFile([string]$LeaseId) {
    $path = Get-LabLeasePath $LeaseId
    if (Test-Path -LiteralPath $path) { return $path }
    $remote = Invoke-LabApi -Path "/leases/$([Uri]::EscapeDataString($LeaseId))"
    (Save-LabLease $remote @()).path
}

function New-Database([string]$Source, [switch]$IncludePop3) {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET SDK selected by global.json and add dotnet to PATH.' }
    if (!$IsWindows) { throw 'generate-db writes DPAPI credentials and runs on Windows only.' }
    Push-Location $repositoryRoot
    try {
        $generatorArguments = @($generatedRoot, $Source)
        if ($IncludePop3) { $generatorArguments += '--pop3' }
        if ($LegacyPlaintext) { $generatorArguments += '--legacy-plaintext' }
        & dotnet run --project (Join-Path $labRoot 'DatabaseGenerator/DatabaseGenerator.csproj') --configuration Debug --property:Platform=x64 -- @generatorArguments |
            ForEach-Object { Protect-LabText "$_" }
        if ($LASTEXITCODE -ne 0) { throw 'Database generation failed. Existing generated artifacts were preserved. Check the build output above.' }
    } finally { Pop-Location }
}

function Invoke-GenerateDatabase {
    $null = Get-LabManifest
    if ($Lease) {
        $path = Get-LeaseFile $Lease
        $leaseDocument = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Write-Output "Generating from lease $Lease."
        New-Database $path -IncludePop3:(@(Get-LabValue $leaseDocument 'pop3Accounts').Count -gt 0)
    } else {
        Write-Output 'Generating from the shared baseline accounts of the manifest.'
        New-Database $manifestPath -IncludePop3:$Pop3
    }
}

function Get-InstallLease {
    $name = Get-HolderName
    $requested = @(Split-List $Servers | Sort-Object)
    foreach ($candidate in Find-HolderLease $name) {
        $fits = (!$requested.Count -or (($requested -join ',') -eq ((@($candidate.servers) | Sort-Object) -join ','))) -and
            (!$boundParameters.ContainsKey('Pop3') -or [bool]$Pop3 -eq (@(Get-LabValue $candidate 'pop3Accounts').Count -gt 0))
        $alive = $false
        if ($fits -and (Test-LabLeaseValid $candidate)) {
            try { $null = Invoke-LabApi -Path "/leases/$([Uri]::EscapeDataString($candidate.id))"; $alive = $true }
            catch { if ($_.Exception.Message -notmatch 'HTTP 404') { throw } }
        }
        if ($alive) {
            $candidate = Update-LabLease $candidate $TtlHours
            Write-Output "Reusing lease $($candidate.id) of $name (renewed). Release it first for freshly reset accounts."
            return $candidate
        }
        Remove-LabLease $candidate.id
        Write-Output "Released lease $($candidate.id) of $name; it expired or does not match the request."
    }
    $created = New-LabLease -Holder $name -Servers $requested -Pop3:$Pop3 -TtlHours $TtlHours
    Format-LabLease $created
    $created
}

function Install-App {
    # The only command that touches application data: it replaces the registered Debug package's database with
    # the generated one after moving the old one to app-backups.
    if (!$IsWindows) { throw 'install-app installs into the Windows Debug package. On a Mac, see tools/lab/macos/README.md.' }
    $package = @(Get-AppxPackage | Where-Object { $_.IsDevelopmentMode -and $_.Name -match 'WinoMail' }) | Select-Object -First 1
    if (!$package) { throw 'No Wino development package is registered. Start the Debug app once (F5), close it, and retry.' }
    if (Get-Process -Name 'Wino.Mail.WinUI' -ErrorAction SilentlyContinue) { throw 'Wino is running. Close it and retry; its data is not replaced while it runs.' }
    $null = Get-LabManifest
    if ($Shared) {
        Write-Output 'Installing the shared baseline accounts. Other agents may change them at the same time.'
        New-Database $manifestPath -IncludePop3:$Pop3
    } else {
        $result = @(Get-InstallLease)
        $result | Where-Object { $_ -is [string] }
        $chosen = $result | Where-Object { $_ -isnot [string] } | Select-Object -Last 1
        New-Database $chosen.path -IncludePop3:(@(Get-LabValue $chosen 'pop3Accounts').Count -gt 0)
    }
    $localState = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState"
    New-Item -ItemType Directory -Path $localState -Force | Out-Null

    $backupRoot = Join-Path $stateRoot "app-backups/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $existing = @(Get-ChildItem -LiteralPath $localState -Force | Where-Object { $_.Name -like 'Wino2*.db*' -or $_.Name -in @('credentials', 'secure') })
    if ($existing.Count) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        foreach ($item in $existing) { Move-Item -LiteralPath $item.FullName -Destination $backupRoot -Force }
        Write-Output "Previous application data moved to $backupRoot"
    }
    Copy-Item -LiteralPath (Join-Path $generatedRoot 'Wino210.db') -Destination $localState -Force
    # The generated folder mirrors LocalState: secure\ (secure item store) or, with -LegacyPlaintext, credentials\dav.
    foreach ($credentialFolder in @('secure', 'credentials')) {
        $source = Join-Path $generatedRoot $credentialFolder
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $localState -Recurse -Force }
    }

    Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!App"
    Write-Output "Wino ($($package.Name)) launched with the lab accounts."
}

function Reset-Account {
    $address = Get-RequiredTarget 'the account address'
    $result = Invoke-LabApi -Method POST -Path ((ConvertTo-LabAddressPath $address) + '/reset') -Query @{ force = if ($Force) { 'true' } }
    Write-Output "Reset $address to the baseline seed."
    if ($Json -and $result) { Write-Result $result }
}

function Show-Inspection {
    $address = Get-RequiredTarget 'the account address'
    $base = ConvertTo-LabAddressPath $address
    if ($Pop3) {
        $rows = @(Invoke-LabApi -Path "$base/pop3")
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows number, uidl, size, subject
    }
    if ($Calendars -or $Calendar) {
        if ($Calendar) {
            $rows = @(Invoke-LabApi -Path "$base/events" -Query @{ calendar = $Calendar })
            if ($Json) { return Write-Result $rows }
            return Write-Table $rows uid, summary, start, end, recurrence, etag
        }
        $rows = @(Invoke-LabApi -Path "$base/calendars")
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows displayName, href, ctag, syncToken, @{ n = 'components'; e = { @($_.components) -join ',' } }
    }
    if ($Contacts -or $AddressBook) {
        if ($AddressBook) {
            $rows = @(Invoke-LabApi -Path "$base/contacts" -Query @{ addressbook = $AddressBook })
            if ($Json) { return Write-Result $rows }
            return Write-Table $rows uid, @{ n = 'name'; e = { Get-LabValue $_ 'name' } }, @{ n = 'email'; e = { Get-LabValue $_ 'email' } }, etag, href
        }
        $rows = @(Invoke-LabApi -Path "$base/addressbooks")
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows displayName, href, ctag, syncToken
    }
    if ($Uid) {
        $messageUid = @(Split-List $Uid)[0]
        $raw = Invoke-LabApi -Path "$base/messages/$messageUid" -Query @{ folder = if ($Folder) { $Folder } else { 'INBOX' } } -As bytes
        if ($Out) {
            [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $raw)
            return "Saved message $messageUid ($($raw.Length) bytes) to $Out."
        }
        return Protect-LabText ([Text.Encoding]::UTF8.GetString($raw))
    }
    if ($Folder) {
        $rows = @(Invoke-LabApi -Path "$base/messages" -Query @{ folder = $Folder; limit = $Limit; sinceUid = if ($SinceUid) { $SinceUid } })
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows uid, @{ n = 'flags'; e = { @($_.flags) -join ' ' } }, modSeq, date, from, subject
    }
    $rows = @(Invoke-LabApi -Path "$base/folders")
    if ($Json) { return Write-Result $rows }
    Write-Table $rows name, messages, unseen, uidValidity, uidNext, highestModSeq, @{ n = 'specialUse'; e = { @($_.specialUse) -join ' ' } }
}

function Get-ContentTypeForFile([string]$Path) {
    switch ([IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        '.txt' { 'text/plain' } '.html' { 'text/html' } '.pdf' { 'application/pdf' } '.png' { 'image/png' }
        { $_ -in '.jpg', '.jpeg' } { 'image/jpeg' } '.gif' { 'image/gif' } '.ics' { 'text/calendar' } '.vcf' { 'text/vcard' }
        '.eml' { 'message/rfc822' } '.zip' { 'application/zip' } default { 'application/octet-stream' }
    }
}

function New-ActionBody([string]$Type) {
    $payload = Read-JsonArgument $Body
    $payload['type'] = $Type
    $values = [ordered]@{
        folder = $Folder; to = $To; from = $From; subject = $Subject; text = $Text; html = $Html; corpus = $Corpus
        method = $Method; style = $Style; summary = $Summary; start = $Start; end = $End; recurrence = $Recurrence
        calendar = $Calendar; addressbook = $AddressBook
    }
    foreach ($name in $values.Keys) { if ($values[$name]) { $payload[$name] = $values[$name] } }
    if ($Uid) { $payload['uids'] = @(Split-List $Uid | ForEach-Object { [long]$_ }) }
    if ($MessageId) { $payload['messageIds'] = @(Split-List $MessageId) }
    if ($Add) { $payload['add'] = @(Split-List $Add) }
    if ($Remove) { $payload['remove'] = @(Split-List $Remove) }
    if ($Count) { $payload['count'] = $Count }
    if ($Expunge) { $payload['expunge'] = $true }
    if ($Attach) {
        $payload['attachments'] = @(foreach ($file in Split-List $Attach) {
                $full = (Resolve-Path -LiteralPath $file).Path
                [ordered]@{ name = [IO.Path]::GetFileName($full); contentType = Get-ContentTypeForFile $full; base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($full)) }
            })
    }
    if ($Type -in 'flag', 'move', 'copy', 'delete', 'bump-uidvalidity' -and !$payload.Contains('folder')) { $payload['folder'] = 'INBOX' }
    $payload
}

function Invoke-Action([string]$Address, [string]$Type, [Collections.IDictionary]$Overrides = @{}) {
    if (!$Type) { throw 'Name the action type: deliver, flag, move, copy, delete, create-folder, delete-folder, rename-folder, bump-uidvalidity, event-create, event-update, event-delete, contact-create, contact-update, contact-delete, send-invite.' }
    $payload = New-ActionBody $Type
    foreach ($name in $Overrides.Keys) {
        if ($null -eq $Overrides[$name]) { $payload.Remove($name) } else { $payload[$name] = $Overrides[$name] }
    }
    $response = Invoke-LabApi -Method POST -Path ((ConvertTo-LabAddressPath $Address) + '/actions') -Body $payload
    $result = Get-LabValue $response 'result'
    "$Type on ${Address}: ok=$(Get-LabValue $response 'ok')$(if ($null -ne $result) { ' ' + (Protect-LabText ($result | ConvertTo-Json -Depth 10 -Compress)) })"
}

function Import-Message {
    $address = Get-RequiredTarget 'the account address'
    if (!$Argument) { throw 'Name the message file: lab.ps1 import <address> <file.eml> [-Folder INBOX] [-Flags \Seen]' }
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Argument).Path)
    $query = [ordered]@{ folder = if ($Folder) { $Folder } else { 'INBOX' }; flags = (Split-List $Flags) -join ' ' }
    $result = Invoke-LabApi -Method POST -Path ((ConvertTo-LabAddressPath $address) + '/import') -Query $query -RawBody $bytes -ContentType 'message/rfc822'
    "Imported $([IO.Path]::GetFileName($Argument)) into $address $($result.folder) as UID $($result.uid)."
}

function Show-Corpus {
    if (!$Target) {
        $rows = @(Invoke-LabApi -Path '/corpus')
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows name, description
    }
    if ($To) { return Invoke-Action $To 'deliver' @{ corpus = $Target; to = $null } }
    $raw = Invoke-LabApi -Path "/corpus/$([Uri]::EscapeDataString($Target))" -As bytes
    if ($Out) {
        [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $raw)
        return "Saved corpus message $Target ($($raw.Length) bytes) to $Out."
    }
    [Text.Encoding]::UTF8.GetString($raw)
}

function Invoke-Fault {
    if ($Clear) {
        $path = if ($Id) { "/faults/$([Uri]::EscapeDataString($Id))" } else { '/faults' }
        Invoke-LabApi -Method DELETE -Path $path | Out-Null
        return $(if ($Id) { "Removed fault $Id." } else { 'Removed every fault.' })
    }
    if (!$Target) {
        $rows = @(Invoke-LabApi -Path '/faults')
        if ($Json) { return Write-Result $rows }
        return Write-Table $rows id, target, kind, @{ n = 'params'; e = { $_.params | ConvertTo-Json -Compress } }, @{ n = 'expires'; e = { Format-LabDate $_.expiresAt } }
    }
    if ($Target -notin 'imap', 'smtp', 'dav') { throw "The fault target is imap, smtp or dav, not '$Target'." }
    if (!$Argument) { throw 'Name the fault kind: latency, bandwidth, timeout, reset, slice or down.' }
    $fault = Invoke-LabApi -Method POST -Path '/faults' -Body ([ordered]@{ target = $Target; kind = $Argument; params = ConvertFrom-ParameterText $Parameters; ttlMinutes = $Minutes })
    "Fault $($fault.id): $Target $Argument on flaky.$labHost until $(Format-LabDate $fault.expiresAt). Clear it with lab.ps1 fault -Clear."
}

function Show-Trace {
    $address = Get-RequiredTarget 'the account address'
    Invoke-LabApi -Path ((ConvertTo-LabAddressPath $address) + '/trace') -Query ([ordered]@{ since = $Since; protocol = $Protocol }) -As text
}

function Save-Ca {
    $path = [IO.Path]::GetFullPath($(if ($Out) { $Out } else { Join-Path $stateRoot 'lab-ca.crt' }))
    $pem = Invoke-LabApi -Path '/ca.crt' -Anonymous -As text
    New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value $pem -Encoding ascii -NoNewline
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem($pem)
    "Lab CA saved to $path"
    "  $($certificate.Subject), SHA-1 $($certificate.Thumbprint), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
    'Trusting it is a manual step that changes the machine''s certificate store; see tools/lab/README.md.'
}

function Start-Tunnel {
    if (!(Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'OpenSSH client (ssh) is required for the tunnel.' }
    $busy = @($tunnelPorts | Where-Object {
            $client = [Net.Sockets.TcpClient]::new()
            try { $client.ConnectAsync('127.0.0.1', $_).Wait(500) -and $client.Connected } catch { $false } finally { $client.Dispose() }
        })
    if ($busy.Count) { throw "Local ports already in use: $($busy -join ', '). Close the other tunnel or local lab first." }
    $forwards = foreach ($port in $tunnelPorts) { '-L'; "127.0.0.1:${port}:127.0.0.1:$port" }
    Write-Output "Forwarding the lab's loopback ports from '$sshHost'. Press Ctrl+C to close the tunnel."
    & ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 @forwards $sshHost
}

function Show-Help {
    @"
Wino mail lab client (PowerShell 7.2+, Windows or macOS). Everything needs only ~/.wino-lab/fixtures.json;
SSH is used by manifest and tunnel alone. Guide: tools/lab/README.md.

.\scripts\lab\windows\lab.ps1 <command> [arguments]

Setup and health
  status | doctor         Endpoint reachability, manifest schema, control API health, local leases.
  manifest                Owner only: fetch the manifest over SSH into ~/.wino-lab/fixtures.json.
  ca [-Out path]          Save the lab CA certificate (for the cert-* hosts).
  tunnel                  Forward the lab's plaintext loopback ports to 127.0.0.1 over SSH.

Leases (private, freshly reset pool accounts)
  lease [-Servers dovecot,stalwart] [-Pop3] [-Holder name] [-TtlHours 24]
                          Lease one account per server; saves ~/.wino-lab/leases/<id>.json (manifest shape).
  leases                  Every lease on the lab; removes local files of leases that ended.
  renew [-Id id] [-TtlHours 24]    Extend a lease (default: this holder's).
  release [-Id id | -All]          Return a lease (default: this holder's; -All: every local lease).

Windows Debug app
  generate-db [-Lease id | -Shared] [-Pop3] [-LegacyPlaintext]
                          Generate Wino210.db and DPAPI credentials into ~/.wino-lab/generated.
  install-app [-Shared] [-Servers ...] [-Pop3] [-Holder name] [-LegacyPlaintext]
                          Lease (or reuse this holder's valid lease), generate, replace the Debug app's data
                          (old data goes to ~/.wino-lab/app-backups) and launch Wino. Wino must be closed.
                          The holder defaults to <computer>/<worktree folder>.

Server truth and changes (act as another device)
  inspect <address> [-Folder INBOX [-Uid n [-Out file]]] [-Calendars [-Calendar href]]
                    [-Contacts [-AddressBook href]] [-Pop3] [-Limit 50] [-SinceUid n] [-Json]
  act <address> <type> [-Body <json or @file>] [-Folder] [-Uid 1,2 | -MessageId ...] [-To] [-Add] [-Remove]
                    [-From] [-Subject] [-Text] [-Html] [-Count] [-Attach files] [-Corpus name] [-Expunge]
                    [-Method REQUEST|CANCEL|REPLY] [-Style outlook|google|icloud|plain] [-Summary] [-Start] [-End]
      types: deliver, flag, move, copy, delete, create-folder, delete-folder, rename-folder, bump-uidvalidity,
             event-create, event-update, event-delete, contact-create, contact-update, contact-delete, send-invite
  import <address> <file.eml> [-Folder INBOX] [-Flags '\Seen']
  reset <address> [-Force]           Restore the baseline seed; baseline accounts need -Force.
  corpus [<name> [-To <address> | -Out file]]   List, fetch or deliver pathological messages.
  fault [<imap|smtp|dav> <kind> [params]] [-Minutes 15] | fault -Clear [-Id id]
      kinds: latency, bandwidth, timeout, reset, slice, down; params as JSON or latency=2000,jitter=200.
      Faults apply to flaky.$labHost (Dovecot logins).
  trace <address> [-Since 10m] [-Protocol imap]   Server-side protocol log, passwords redacted.

Settings (environment)
  WINO_LAB_HOST      public DNS suffix of the lab (default lab.winomail.app)
  WINO_LAB_SSH_HOST  SSH host or alias of the lab machine (default wino-lab); needed by manifest and tunnel
  WINO_LAB_STATE     folder for the manifest, leases and generated data (default ~/.wino-lab)
"@
}

try {
    switch ($Command) {
        'help' { Show-Help }
        { $_ -in 'status', 'doctor' } { Show-Status }
        'manifest' { Get-Manifest }
        'lease' { New-Lease }
        'leases' { Show-Leases }
        'renew' { Update-Lease }
        'release' { Remove-Lease }
        'generate-db' { Invoke-GenerateDatabase }
        'install-app' { Install-App }
        'reset' { Reset-Account }
        'inspect' { Show-Inspection }
        'act' { Invoke-Action (Get-RequiredTarget 'the account address') $Argument }
        'import' { Import-Message }
        'corpus' { Show-Corpus }
        'fault' { Invoke-Fault }
        'trace' { Show-Trace }
        'ca' { Save-Ca }
        'tunnel' { Start-Tunnel }
    }
} catch {
    $ErrorActionPreference = 'Continue'
    Write-Error (Protect-LabText $_.Exception.Message)
    exit 1
}
