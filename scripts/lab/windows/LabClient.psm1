#requires -Version 7.2
# Shared by lab.ps1 and the Sandbox helper (start-wino.ps1). The binding contract for the manifest and the control
# API is docs/contract.md in the lab repository.
Set-StrictMode -Version Latest

$script:StateRoot = if ($env:WINO_LAB_STATE) { $env:WINO_LAB_STATE } else { Join-Path $HOME '.wino-lab' }
$script:ManifestPath = Join-Path $script:StateRoot 'fixtures.json'
$script:LeaseRoot = Join-Path $script:StateRoot 'leases'
$script:LabHost = if ($env:WINO_LAB_HOST) { $env:WINO_LAB_HOST } else { 'lab.winomail.app' }
$script:Manifest = $null

function Get-LabStateRoot { $script:StateRoot }
function Get-LabManifestPath { $script:ManifestPath }
function Get-LabHostName { $script:LabHost }

function Get-LabValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { $property.Value } else { $null }
}

function Get-LabManifest([switch]$Optional, [switch]$Reload) {
    if ($script:Manifest -and !$Reload) { return $script:Manifest }
    if (!(Test-Path -LiteralPath $script:ManifestPath -PathType Leaf)) {
        if ($Optional) { return $null }
        throw "The lab manifest $($script:ManifestPath) does not exist, so this machine is not set up for the lab. The owner sets it up with lab.ps1 manifest."
    }
    $script:Manifest = Get-Content -LiteralPath $script:ManifestPath -Raw | ConvertFrom-Json
    $script:Manifest
}

function Get-LabManifestIssue($Manifest) {
    $schema = Get-LabValue $Manifest 'schema'
    $control = Get-LabValue $Manifest 'control'
    if (!$schema -or [int]$schema -lt 2 -or !(Get-LabValue $control 'url') -or !(Get-LabValue $control 'token')) {
        return "The manifest is schema $(if ($schema) { $schema } else { 1 }) and has no control API settings. Rerun lab.ps1 manifest (the owner fetches it over SSH)."
    }
}

function Get-LabControlUrl {
    $control = Get-LabValue (Get-LabManifest -Optional) 'control'
    $url = Get-LabValue $control 'url'
    if ($url) { $url.TrimEnd('/') } else { "https://control.$($script:LabHost)/v1" }
}

# Keeps the lab password and the control token out of everything the client prints.
function Protect-LabText([string]$Text) {
    if (!$Text) { return $Text }
    $manifest = Get-LabManifest -Optional
    foreach ($secret in @((Get-LabValue $manifest 'password'), (Get-LabValue (Get-LabValue $manifest 'control') 'token'))) {
        if ($secret -is [string] -and $secret.Length -ge 4) { $Text = $Text.Replace($secret, '***') }
    }
    $Text
}

function Invoke-LabApi {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [Collections.IDictionary]$Query,
        $Body,
        [byte[]]$RawBody,
        [string]$ContentType = 'application/json; charset=utf-8',
        [switch]$Anonymous,
        [ValidateSet('json', 'text', 'bytes')][string]$As = 'json'
    )
    $headers = @{ Accept = if ($As -eq 'json') { 'application/json' } else { '*/*' } }
    if (!$Anonymous) {
        $manifest = Get-LabManifest
        $issue = Get-LabManifestIssue $manifest
        if ($issue) { throw $issue }
        $headers.Authorization = "Bearer $($manifest.control.token)"
    }
    $baseUrl = Get-LabControlUrl
    $uri = $baseUrl + $Path
    if ($Query) {
        $pairs = foreach ($key in $Query.Keys) {
            if ($null -ne $Query[$key] -and "$($Query[$key])" -ne '') { "$key=$([Uri]::EscapeDataString([string]$Query[$key]))" }
        }
        if ($pairs) { $uri += '?' + ($pairs -join '&') }
    }
    $request = @{ Uri = $uri; Method = $Method; Headers = $headers; SkipHttpErrorCheck = $true; TimeoutSec = 180 }
    if ($null -ne $RawBody) { $request.Body = $RawBody; $request.ContentType = $ContentType }
    elseif ($null -ne $Body) {
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 20 -Compress))
        $request.ContentType = 'application/json; charset=utf-8'
    }

    $ProgressPreference = 'SilentlyContinue'
    try { $response = Invoke-WebRequest @request }
    catch { throw "The lab is unreachable at ${baseUrl}: $($_.Exception.Message)" }

    $status = [int]$response.StatusCode
    $bytes = $response.RawContentStream.ToArray()
    $content = [Text.Encoding]::UTF8.GetString($bytes)
    if ($status -ge 400) {
        $detail = $content
        try { $parsed = $content | ConvertFrom-Json; if (Get-LabValue $parsed 'error') { $detail = $parsed.error } } catch { }
        $message = switch ($status) {
            { $_ -in 401, 403 } { "The lab rejected the control token (HTTP $status). The manifest is out of date: rerun lab.ps1 manifest (the owner fetches it over SSH)."; break }
            501 { "The lab does not implement $Method $Path yet (HTTP 501)."; break }
            default { "$Method $Path failed with HTTP ${status}: $detail" }
        }
        throw (Protect-LabText $message)
    }
    switch ($As) {
        'bytes' { , $bytes }
        'text' { Protect-LabText $content }
        default { if ($content.Trim()) { $content | ConvertFrom-Json } }
    }
}

function ConvertTo-LabAddressPath([string]$Address) {
    if (!$Address -or $Address -notmatch '@') { throw "Name the lab account by its address, for example alice1@dovecot.test." }
    "/accounts/$([Uri]::EscapeDataString($Address))"
}

function Get-LabDefaultHolder([string]$Name) {
    $machine = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { [Environment]::MachineName }
    "$machine/$Name"
}

function Get-LabLeasePath([string]$Id) {
    Join-Path $script:LeaseRoot (($Id -replace '[^\w.-]', '_') + '.json')
}

function Get-LabLocalLease {
    if (!(Test-Path -LiteralPath $script:LeaseRoot)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $script:LeaseRoot -Filter '*.json' -File) {
        try {
            $lease = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $lease | Add-Member -NotePropertyName path -NotePropertyValue $file.FullName -Force
            $lease
        } catch { Write-Warning "Skipping unreadable lease file $($file.FullName)." }
    }
}

function Test-LabLeaseValid($Lease, [TimeSpan]$Margin = [TimeSpan]::FromMinutes(30)) {
    $expires = Get-LabValue $Lease 'expiresAt'
    $expires -and ((ConvertTo-LabDate $expires) -gt [DateTimeOffset]::UtcNow.Add($Margin))
}

# ConvertFrom-Json turns ISO 8601 strings into DateTime values; both forms arrive here.
function ConvertTo-LabDate($Value) {
    if ($Value -is [DateTime]) { return [DateTimeOffset]$Value }
    if ($Value -is [DateTimeOffset]) { return $Value }
    [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Format-LabDate($Value) { (ConvertTo-LabDate $Value).ToLocalTime().ToString('yyyy-MM-dd HH:mm') }

# A lease file has the manifest's shape so DatabaseGenerator and the Mac debug bridge read it unchanged.
function Save-LabLease($Lease, [string[]]$Servers) {
    $manifest = Get-LabManifest
    $document = [ordered]@{
        schema = 2
        kind = 'lease'
        id = $Lease.id
        holder = $Lease.holder
        createdAt = Get-LabValue $Lease 'createdAt'
        expiresAt = $Lease.expiresAt
        servers = @($Servers | Where-Object { $_ })
        anchorDate = Get-LabValue $manifest 'anchorDate'
        labHost = Get-LabValue $manifest 'labHost'
        password = if (Get-LabValue $Lease 'password') { $Lease.password } else { $manifest.password }
        accounts = @(Get-LabValue $Lease 'accounts' | Where-Object { $_ })
        pop3Accounts = @(Get-LabValue $Lease 'pop3Accounts' | Where-Object { $_ })
    }
    New-Item -ItemType Directory -Path $script:LeaseRoot -Force | Out-Null
    $path = Get-LabLeasePath $Lease.id
    Set-Content -LiteralPath $path -Value ($document | ConvertTo-Json -Depth 20) -Encoding utf8NoBOM
    Get-LabLocalLease | Where-Object { $_.path -eq $path }
}

function New-LabLease([string]$Holder, [string[]]$Servers, [switch]$Pop3, [int]$TtlHours = 24) {
    $body = [ordered]@{ holder = $Holder; pop3 = [bool]$Pop3; ttlHours = $TtlHours }
    if ($Servers) { $body.servers = @($Servers) }
    $lease = Invoke-LabApi -Method POST -Path '/leases' -Body $body
    Save-LabLease $lease $Servers
}

function Update-LabLease($Lease, [int]$TtlHours = 24) {
    $renewed = Invoke-LabApi -Method POST -Path "/leases/$([Uri]::EscapeDataString($Lease.id))/renew" -Body @{ ttlHours = $TtlHours }
    $document = Get-Content -LiteralPath $Lease.path -Raw | ConvertFrom-Json -AsHashtable
    $document.expiresAt = $renewed.expiresAt
    Set-Content -LiteralPath $Lease.path -Value ($document | ConvertTo-Json -Depth 20) -Encoding utf8NoBOM
    $Lease.expiresAt = $renewed.expiresAt
    $Lease
}

function Remove-LabLease([string]$Id) {
    try { Invoke-LabApi -Method DELETE -Path "/leases/$([Uri]::EscapeDataString($Id))" | Out-Null }
    catch { if ($_.Exception.Message -notmatch 'HTTP 404') { throw } }
    $path = Get-LabLeasePath $Id
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

function Format-LabLease($Lease) {
    $expires = Format-LabDate $Lease.expiresAt
    "Lease $($Lease.id) for $($Lease.holder), expires $expires"
    foreach ($account in @(Get-LabValue $Lease 'accounts' | Where-Object { $_ })) {
        $dav = if (Get-LabValue $account 'davUrl') { 'DAV' } else { 'no DAV' }
        '  {0,-12} {1,-28} {2}:{3}  {4}' -f (Get-LabValue $account 'key'), (Get-LabValue $account 'address'), (Get-LabValue $account 'imapHost'), (Get-LabValue $account 'imapPort'), $dav
    }
    foreach ($account in @(Get-LabValue $Lease 'pop3Accounts' | Where-Object { $_ })) {
        '  {0,-12} {1,-28} {2}:{3}  POP3' -f (Get-LabValue $account 'key'), (Get-LabValue $account 'address'), (Get-LabValue $account 'pop3Host'), (Get-LabValue $account 'pop3Port')
    }
    $path = Get-LabValue $Lease 'path'
    if ($path) { "  File: $path (manifest shape; holds the lab password)" }
}

Export-ModuleMember -Function Get-LabStateRoot, Get-LabManifestPath, Get-LabHostName, Get-LabValue, Get-LabManifest,
    Get-LabManifestIssue, Get-LabControlUrl, Protect-LabText, Invoke-LabApi, ConvertTo-LabAddressPath, Get-LabDefaultHolder,
    Get-LabLeasePath, Get-LabLocalLease, ConvertTo-LabDate, Format-LabDate, Test-LabLeaseValid, Save-LabLease, New-LabLease, Update-LabLease, Remove-LabLease,
    Format-LabLease
