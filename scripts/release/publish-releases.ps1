#requires -Version 7.0
<#
.SYNOPSIS
Publishes built Beta and Sideload releases to https://download.winomail.app.
.DESCRIPTION
Lists the versions under the release output folder (see build-releases.ps1), shows which
channels each version contains and which version each channel currently serves, and uploads
the selected channels to the Cloudflare R2 bucket behind download.winomail.app.

Bundles and dependency packages go to <bundle name>/<file>, so a published version is never
replaced. Each channel's .appinstaller feed is uploaded last, after every package it references
is in place. Uploads use R2's S3-compatible API because bundles exceed the 300 MB single-request
limit of the Cloudflare API and wrangler. See docs/releases.md.
#>
[CmdletBinding()]
param(
    [string]$ReleasesRoot = $(if ($env:WINO_RELEASES_ROOT) { $env:WINO_RELEASES_ROOT } else { 'D:\Wino Releases' }),
    [string]$Version,
    [ValidateSet('Beta', 'Sideload')][string[]]$Channels,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:PublicBaseUri = 'https://download.winomail.app/'
$script:Bucket = 'wino-downloads'
$script:Feeds = [ordered]@{ Beta = 'WinoMailBetaIsolated.appinstaller'; Sideload = 'WinoMail.appinstaller' }
$script:PartSize = 64MB
$script:Http = [Net.Http.HttpClient]::new()
$script:Http.Timeout = [TimeSpan]::FromMinutes(30)

function Read-PublishChoice {
    param([string]$Prompt, [hashtable]$Choices)

    while ($true) {
        $answer = Read-Host $Prompt
        if ($null -eq $answer) { throw 'Input ended before all selections were supplied.' }
        $answer = $answer.Trim().ToLowerInvariant()
        if ($Choices.ContainsKey($answer)) { return $Choices[$answer] }
        Write-Host 'Select one of the displayed options.' -ForegroundColor Yellow
    }
}

function Get-PublishCredentials {
    $values = @{}
    foreach ($name in @('WINO_R2_ACCOUNT_ID', 'WINO_R2_ACCESS_KEY_ID', 'WINO_R2_SECRET_ACCESS_KEY')) {
        $values[$name] = [Environment]::GetEnvironmentVariable($name)
        if ([string]::IsNullOrWhiteSpace($values[$name])) { throw "Set $name before publishing. See docs/local-script-environment.md." }
    }
    return [pscustomobject]@{
        Endpoint = "https://$($values.WINO_R2_ACCOUNT_ID).r2.cloudflarestorage.com"
        AccessKeyId = $values.WINO_R2_ACCESS_KEY_ID
        SecretAccessKey = $values.WINO_R2_SECRET_ACCESS_KEY
    }
}

function Get-ChannelRelease {
    param([string]$Folder, [string]$Channel, [string]$Version)

    $feedName = $script:Feeds[$Channel]
    $feedPath = Join-Path $Folder $feedName
    if (-not (Test-Path -LiteralPath $feedPath -PathType Leaf)) { return $null }
    $feed = [xml](Get-Content -LiteralPath $feedPath -Raw)
    $expectedFeedUri = $script:PublicBaseUri + $feedName
    if ([string]$feed.AppInstaller.Uri -cne $expectedFeedUri) {
        throw "$feedPath points at $($feed.AppInstaller.Uri), expected $expectedFeedUri. Rebuild the release with the current build script."
    }
    if ([string]$feed.AppInstaller.Version -cne $Version) { throw "$feedPath has version $($feed.AppInstaller.Version), but its folder is $Version." }

    # Upload exactly what the feed references: the bundle plus any dependency packages, all under the bundle's directory.
    $packageUris = @([string]$feed.AppInstaller.MainBundle.Uri)
    $packageUris += @($feed.SelectNodes("//*[local-name()='Dependencies']/*[local-name()='Package']") | ForEach-Object { [string]$_.GetAttribute('Uri') })
    $bundleKey = $packageUris[0].Substring([Math]::Min($packageUris[0].Length, $script:PublicBaseUri.Length))
    $directory = $bundleKey.Split('/')[0] + '/'
    $files = foreach ($uri in $packageUris) {
        if (-not $uri.StartsWith($script:PublicBaseUri + $directory, [StringComparison]::Ordinal)) {
            throw "$feedPath references $uri, which is outside $($script:PublicBaseUri)$directory."
        }
        $key = [Uri]::UnescapeDataString($uri.Substring($script:PublicBaseUri.Length))
        $relative = $key.Substring($directory.Length)
        $local = [IO.Path]::GetFullPath((Join-Path $Folder $relative))
        if (-not $local.StartsWith([IO.Path]::GetFullPath($Folder), [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $local -PathType Leaf)) {
            throw "$feedPath references $relative, which is missing from $Folder."
        }
        $contentType = switch ([IO.Path]::GetExtension($local).ToLowerInvariant()) {
            '.msixbundle' { 'application/msixbundle' } '.appxbundle' { 'application/appxbundle' }
            '.msix' { 'application/msix' } '.appx' { 'application/appx' }
            default { throw "Unexpected package type: $local" }
        }
        [pscustomobject]@{ Key = $key; Path = $local; ContentType = $contentType; Size = (Get-Item -LiteralPath $local).Length }
    }
    return [pscustomobject]@{
        Channel = $Channel; Version = $Version; Folder = $Folder; Packages = @($files)
        Feed = [pscustomobject]@{ Key = $feedName; Path = $feedPath; ContentType = 'application/appinstaller'; Size = (Get-Item -LiteralPath $feedPath).Length }
    }
}

function Get-AvailableReleases {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "The release folder does not exist: $Root" }
    $releases = foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory) {
        $parsed = $null
        if (-not [version]::TryParse($directory.Name, [ref]$parsed)) { continue }
        $channels = [ordered]@{}
        foreach ($channel in $script:Feeds.Keys) {
            $folder = Join-Path $directory.FullName $channel
            if (Test-Path -LiteralPath $folder -PathType Container) {
                try {
                    $release = Get-ChannelRelease $folder $channel $directory.Name
                    if ($null -ne $release) { $channels[$channel] = $release }
                }
                catch { Write-Warning "Skipping $($directory.Name) $channel`: $($_.Exception.Message)" }
            }
        }
        [pscustomobject]@{ Version = $directory.Name; SortKey = $parsed; Channels = $channels; HasStore = Test-Path -LiteralPath (Join-Path $directory.FullName 'Store') }
    }
    return @($releases | Sort-Object SortKey -Descending)
}

function Get-LiveVersion {
    param([string]$Channel)

    try {
        $request = [Net.Http.HttpRequestMessage]::new('GET', $script:PublicBaseUri + $script:Feeds[$Channel])
        $request.Headers.CacheControl = [Net.Http.Headers.CacheControlHeaderValue]::new()
        $request.Headers.CacheControl.NoCache = $true
        $response = $script:Http.Send($request)
        if (-not $response.IsSuccessStatusCode) { return $null }
        return [string]([xml]$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).AppInstaller.Version
    }
    catch { return $null }
}

function Get-PublicObject {
    param([string]$Key)

    $escaped = ($Key.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $request = [Net.Http.HttpRequestMessage]::new('HEAD', $script:PublicBaseUri + $escaped)
    $response = $script:Http.Send($request)
    if (-not $response.IsSuccessStatusCode) { return $null }
    return [pscustomobject]@{
        Size = $response.Content.Headers.ContentLength
        ContentType = [string]$response.Content.Headers.ContentType
    }
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-HmacSha256 {
    param([byte[]]$Key, [string]$Data)
    return [Security.Cryptography.HMACSHA256]::HashData($Key, [Text.Encoding]::UTF8.GetBytes($Data))
}

function Invoke-R2Request {
    param(
        [object]$Credentials, [string]$Method, [string]$Key, [System.Collections.Specialized.OrderedDictionary]$Query = [ordered]@{},
        [byte[]]$Body = [byte[]]::new(0), [hashtable]$ContentHeaders = @{}
    )

    # AWS Signature Version 4 for R2 (region "auto"). Only host and x-amz-* headers are signed.
    $endpoint = [Uri]$Credentials.Endpoint
    $path = "/$($script:Bucket)/" + (($Key.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $queryText = (@($Query.Keys | Sort-Object -CaseSensitive | ForEach-Object {
        '{0}={1}' -f [Uri]::EscapeDataString($_), [Uri]::EscapeDataString([string]$Query[$_])
    }) -join '&')
    $now = [DateTime]::UtcNow
    $amzDate = $now.ToString("yyyyMMdd'T'HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $date = $now.ToString('yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
    $payloadHash = Get-Sha256Hex $Body
    $canonicalHeaders = "host:$($endpoint.Host)`nx-amz-content-sha256:$payloadHash`nx-amz-date:$amzDate`n"
    $signedHeaders = 'host;x-amz-content-sha256;x-amz-date'
    $canonicalRequest = "$Method`n$path`n$queryText`n$canonicalHeaders`n$signedHeaders`n$payloadHash"
    $scope = "$date/auto/s3/aws4_request"
    $stringToSign = "AWS4-HMAC-SHA256`n$amzDate`n$scope`n$(Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($canonicalRequest)))"
    $signingKey = [Text.Encoding]::UTF8.GetBytes("AWS4$($Credentials.SecretAccessKey)")
    foreach ($part in @($date, 'auto', 's3', 'aws4_request')) { $signingKey = Get-HmacSha256 $signingKey $part }
    $signature = [Convert]::ToHexString((Get-HmacSha256 $signingKey $stringToSign)).ToLowerInvariant()

    $uri = "$($endpoint.GetLeftPart([UriPartial]::Authority))$path" + $(if ($queryText) { "?$queryText" } else { '' })
    $request = [Net.Http.HttpRequestMessage]::new($Method, $uri)
    $null = $request.Headers.TryAddWithoutValidation('x-amz-date', $amzDate)
    $null = $request.Headers.TryAddWithoutValidation('x-amz-content-sha256', $payloadHash)
    $null = $request.Headers.TryAddWithoutValidation('Authorization', "AWS4-HMAC-SHA256 Credential=$($Credentials.AccessKeyId)/$scope, SignedHeaders=$signedHeaders, Signature=$signature")
    $request.Content = [Net.Http.ByteArrayContent]::new($Body)
    foreach ($name in $ContentHeaders.Keys) {
        # Content-Type belongs to the content; Cache-Control is a request header. R2 stores both on the object.
        if (-not $request.Content.Headers.TryAddWithoutValidation($name, $ContentHeaders[$name])) {
            $null = $request.Headers.TryAddWithoutValidation($name, $ContentHeaders[$name])
        }
    }

    $response = $script:Http.Send($request)
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "R2 $Method $Key failed with $([int]$response.StatusCode): $text" }
    return [pscustomobject]@{ Response = $response; Text = $text }
}

function Invoke-WithRetry {
    param([scriptblock]$Action, [string]$Description)

    for ($attempt = 1; ; $attempt++) {
        try { return & $Action }
        catch {
            if ($attempt -ge 3) { throw }
            Write-Warning "$Description failed (attempt $attempt of 3): $($_.Exception.Message). Retrying…"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

function Send-ReleaseFile {
    param([object]$Credentials, [object]$File, [string]$CacheControl)

    $headers = @{ 'Content-Type' = $File.ContentType; 'Cache-Control' = $CacheControl }
    $label = "$($File.Key) ($([Math]::Round($File.Size / 1MB, 1)) MB)"
    if ($File.Size -le $script:PartSize) {
        Write-Host "Uploading $label"
        $bytes = [IO.File]::ReadAllBytes($File.Path)
        $null = Invoke-WithRetry { Invoke-R2Request $Credentials 'PUT' $File.Key -Body $bytes -ContentHeaders $headers } "Upload of $($File.Key)"
        return
    }

    $initiate = Invoke-R2Request $Credentials 'POST' $File.Key -Query ([ordered]@{ uploads = '' }) -ContentHeaders $headers
    $uploadId = ([xml]$initiate.Text).InitiateMultipartUploadResult.UploadId
    $parts = [Collections.Generic.List[string]]::new()
    $stream = [IO.File]::OpenRead($File.Path)
    try {
        $count = [int][Math]::Ceiling($File.Size / $script:PartSize)
        for ($number = 1; $number -le $count; $number++) {
            Write-Progress -Activity "Uploading $label" -Status "Part $number of $count" -PercentComplete (($number - 1) * 100 / $count)
            $buffer = [byte[]]::new([Math]::Min($script:PartSize, $File.Size - $stream.Position))
            $stream.ReadExactly($buffer, 0, $buffer.Length)
            $query = [ordered]@{ partNumber = "$number"; uploadId = $uploadId }
            $result = Invoke-WithRetry { Invoke-R2Request $Credentials 'PUT' $File.Key -Query $query -Body $buffer } "Part $number of $($File.Key)"
            $parts.Add("<Part><PartNumber>$number</PartNumber><ETag>$($result.Response.Headers.ETag.Tag)</ETag></Part>")
        }
        Write-Progress -Activity "Uploading $label" -Completed
        $complete = [Text.Encoding]::UTF8.GetBytes("<CompleteMultipartUpload>$($parts -join '')</CompleteMultipartUpload>")
        $null = Invoke-R2Request $Credentials 'POST' $File.Key -Query ([ordered]@{ uploadId = $uploadId }) -Body $complete -ContentHeaders @{ 'Content-Type' = 'application/xml' }
        Write-Host "Uploaded $label"
    }
    catch {
        try { $null = Invoke-R2Request $Credentials 'DELETE' $File.Key -Query ([ordered]@{ uploadId = $uploadId }) } catch { }
        throw
    }
    finally { $stream.Dispose() }
}

function Publish-Releases {
    param([object[]]$Selected)

    $credentials = Get-PublishCredentials
    # Packages first for every channel; feeds last, so no feed ever points at a package that is not online yet.
    foreach ($release in $Selected) {
        foreach ($file in $release.Packages) {
            $existing = Get-PublicObject $file.Key
            if ($null -ne $existing) {
                if ($existing.Size -ne $file.Size) {
                    throw "$($script:PublicBaseUri)$($file.Key) is already published with a different size. Published versions are never replaced; build a new version."
                }
                Write-Host "Already published: $($file.Key)"
                continue
            }
            Send-ReleaseFile $credentials $file 'public, max-age=31536000, immutable'
        }
    }
    foreach ($release in $Selected) {
        foreach ($file in $release.Packages) {
            $published = Get-PublicObject $file.Key
            if ($null -eq $published -or $published.Size -ne $file.Size -or $published.ContentType -ne $file.ContentType) {
                throw "$($script:PublicBaseUri)$($file.Key) is not publicly available as expected. The $($release.Channel) feed was not updated."
            }
        }
        Send-ReleaseFile $credentials $release.Feed 'no-cache'
        $live = Get-LiveVersion $release.Channel
        if ($live -cne $release.Version) { throw "The $($release.Channel) feed serves version '$live' after upload, expected $($release.Version)." }
        Write-Host "$($release.Channel) now serves $($release.Version): $($script:PublicBaseUri)$($release.Feed.Key)" -ForegroundColor Green
    }
}

function Invoke-InteractivePublish {
    $releases = @(Get-AvailableReleases ([IO.Path]::GetFullPath($ReleasesRoot)) | Where-Object { $_.Channels.Count -gt 0 })
    if ($releases.Count -eq 0) { Write-Host "No Beta or Sideload releases found under $ReleasesRoot."; return }

    $live = [ordered]@{}
    foreach ($channel in $script:Feeds.Keys) { $live[$channel] = Get-LiveVersion $channel }
    Write-Host "Currently published: $(@($live.Keys | ForEach-Object { "$_ $(if ($live[$_]) { $live[$_] } else { '(none)' })" }) -join ' | ')"
    Write-Host ''
    for ($index = 0; $index -lt $releases.Count; $index++) {
        $release = $releases[$index]
        $columns = foreach ($channel in $script:Feeds.Keys) { '{0,-9}' -f $(if ($release.Channels.Contains($channel)) { $channel } else { '-' }) }
        Write-Host ('  {0}) {1,-12} {2} {3}' -f ($index + 1), $release.Version, ($columns -join ' '), $(if ($release.HasStore) { '(Store build is local only)' } else { '' }))
    }
    Write-Host ''

    $release = if ($Version) {
        $releases | Where-Object Version -eq $Version | Select-Object -First 1
    } elseif ($NonInteractive) { $releases[0] } else {
        $choices = @{}
        for ($index = 0; $index -lt $releases.Count; $index++) { $choices["$($index + 1)"] = $releases[$index]; $choices[$releases[$index].Version] = $releases[$index] }
        $choices[''] = $releases[0]
        Read-PublishChoice "Version to publish? (number, Enter = $($releases[0].Version))" $choices
    }
    if ($null -eq $release) { throw "Version $Version has no Beta or Sideload release under $ReleasesRoot." }

    $available = @($release.Channels.Keys)
    $channels = if ($Channels) { $Channels } elseif ($NonInteractive) { $available } else {
        $choices = @{ a = $available; all = $available }
        foreach ($channel in $available) { $choices[$channel.ToLowerInvariant()] = @($channel); $choices[$channel.Substring(0, 1).ToLowerInvariant()] = @($channel) }
        $options = (@($available | ForEach-Object { "$($_.Substring(0, 1).ToLowerInvariant()) = $_" }) + 'a = all') -join ', '
        Read-PublishChoice "Channels to publish for $($release.Version)? ($options)" $choices
    }
    $selected = foreach ($channel in $channels) {
        if (-not $release.Channels.Contains($channel)) { throw "$($release.Version) has no $channel release." }
        $release.Channels[$channel]
    }

    Write-Host ''
    foreach ($item in $selected) {
        $current = $live[$item.Channel]
        $note = if (-not $current) { 'first publication' }
            elseif ([version]$current -eq [version]$item.Version) { 'republish of the live version' }
            elseif ([version]$current -gt [version]$item.Version) { "DOWNGRADE from $current; installed apps will not move back" }
            else { "update from $current" }
        $size = ($item.Packages | Measure-Object Size -Sum).Sum / 1MB
        Write-Host ('{0}: {1} ({2:N0} MB) - {3}' -f $item.Channel, $item.Version, $size, $note)
    }
    if (-not $NonInteractive) {
        $confirm = Read-PublishChoice 'Publish? (yes/no)' @{ yes = $true; y = $true; no = $false; n = $false }
        if (-not $confirm) { Write-Host 'Nothing was published.'; return }
    }
    Publish-Releases @($selected)
}

# Dot-sourcing exposes functions for the script tests without prompting or uploading.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-InteractivePublish } catch { Write-Error $_ -ErrorAction Continue; exit 1 }
}
