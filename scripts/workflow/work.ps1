#requires -Version 7.5
<#
.SYNOPSIS
Plumbing for the unattended agent workflow: issue worktrees, worker launch, the work site on R2, Discord posts, and the scheduled tick.
.DESCRIPTION
State and artifacts live under the work root (D:\Wino-Work, override with WINO_WORK_ROOT). The design is docs/agentic-workflow.md.
Run "work.ps1 help" for the commands.
.EXAMPLE
pwsh -NoProfile -File scripts/workflow/work.ps1 start 1111 -Effort high -Instructions "Keep the old shortcut"
.EXAMPLE
pwsh -NoProfile -File scripts/workflow/work.ps1 publish 1111 D:\Wino-Work\issues\1111\plan.md -As plan
.EXAMPLE
pwsh -NoProfile -File scripts/workflow/work.ps1 tick
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Arguments = @(),
    [string]$Model,
    [ValidateSet('low', 'medium', 'high', 'xhigh', 'max')]
    [string]$Effort,
    [ValidateRange(0, 9)]
    [int]$Design = 0,
    [string]$Instructions,
    [switch]$NoLaunch,
    [double]$TimeoutHours = 0,
    [ValidateSet('plan', 'design', 'screen', 'video', 'report', 'other')]
    [string]$As,
    [string]$Name,
    [switch]$Critical
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:WorkRoot = if ($env:WINO_WORK_ROOT) { $env:WINO_WORK_ROOT } else { 'D:\Wino-Work' }
$script:ConfigPath = Join-Path $script:WorkRoot 'config.json'
$script:StatePath = Join-Path $script:WorkRoot 'state.json'
$script:TemplateRoot = Join-Path $PSScriptRoot 'templates'
$script:Utf8 = [Text.UTF8Encoding]::new($false)
$script:ActiveStatuses = @('queued', 'planning', 'implementing', 'verifying', 'parity')
$script:KindTitles = [ordered]@{ plan = 'Plan'; design = 'Designs'; screen = 'Screenshots'; video = 'Recordings'; report = 'Report'; other = 'Other files' }
$script:KindFolders = @{ design = 'designs/'; screen = 'screens/'; video = 'videos/' }
$script:ContentTypes = @{
    '.html' = 'text/html; charset=utf-8'; '.htm' = 'text/html; charset=utf-8'; '.json' = 'application/json; charset=utf-8'
    '.png' = 'image/png'; '.jpg' = 'image/jpeg'; '.jpeg' = 'image/jpeg'; '.gif' = 'image/gif'; '.svg' = 'image/svg+xml'
    '.mp4' = 'video/mp4'; '.webm' = 'video/webm'; '.txt' = 'text/plain; charset=utf-8'
}
$script:Config = $null
$script:Http = $null

$script:HelpText = @'
Usage: pwsh -NoProfile -File scripts/workflow/work.ps1 <command> [arguments] [options]

Setup and overview
  init                          Create the work root, config.json (never overwritten), and state.json.
  status                        Issues with status, step, worktree, worker, parity; Mac and queue summary.
  pause | resume                Stop or allow idle prototyping. start still works while paused.

Issues
  start <N> [-Model <id>] [-Effort low|medium|high|xhigh|max] [-Design <count>] [-Instructions "<text>"] [-NoLaunch]
                                Label the issue queued, create worktree wt\<N> on agent/<N>-<slug> from origin/main
                                (or reuse it), write brief.md, create the Discord thread, launch the worker
                                (claude -p --dangerously-skip-permissions --model <id> --effort <level>).
  stop <N>                      Kill the worker process tree and mark the issue paused.
  ask <N> "<text>" [-TimeoutHours <h>]
                                Post a question and wait for answer.md (exit code 2 on timeout).
  answer <N> "<text>"           Write the owner's answer for a waiting worker.
  publish <N> <path> [-As plan|design|screen|video|report|other] [-Name <name>]
                                Upload to R2 under <N>/ (Markdown is rendered to HTML) and rebuild the indexes.

Discord
  notify <N> "<text>"           Post to the issue thread, or the issues channel without one.
  alert "<text>" [-Critical]    Post to the alerts channel.
                                Outside -Critical, posts in quiet hours are queued and sent by tick.
  brief                         Post the morning brief.

Scheduled
  tick                          Reap workers, kill builds over the time cap, prune pushed worktrees,
                                ping the Mac, flush queued posts.
  boot                          After a reboot, alert and mark interrupted workers paused.

Mac
  mac ping                      Exit code 0 when the Mac answers over SSH, 1 otherwise.
  mac sync|build|test           Not implemented (phase 4).

Not implemented yet (exit code 3): hardstops, merge, parity (phase 3/4), prototype (phase 5), release (phase 7).

Work root: D:\Wino-Work, or WINO_WORK_ROOT. Settings: <work root>\config.json.
Secrets come only from the environment: WINO_R2_ACCOUNT_ID, WINO_R2_ACCESS_KEY_ID, WINO_R2_SECRET_ACCESS_KEY,
and WINO_WORK_DISCORD_BOT_TOKEN (falls back to DISCORD_BOT_TOKEN in ~/.claude/channels/discord/.env,
which the Discord channel plugin writes). The bot token is needed only to create issue threads.
'@

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    $temporary = "$Path.$PID.tmp"
    [IO.File]::WriteAllText($temporary, $Text, $script:Utf8)
    [IO.File]::Move($temporary, $Path, $true)
}

function Read-JsonFile {
    param([string]$Path)
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
}

function Get-UtcStamp { return [DateTime]::UtcNow.ToString('o') }

function ConvertTo-UtcDate {
    param($Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

function Get-DefaultConfig {
    return [ordered]@{
        repoPath = 'D:\Wino-Mail'
        macHost = 'burak-mac'
        sshExe = 'C:\Windows\System32\OpenSSH\ssh.exe'
        timeZone = 'Central European Standard Time'
        quietHours = [ordered]@{ start = '02:00'; end = '08:00' }
        briefTime = '07:30'
        r2 = [ordered]@{ bucket = 'wino-work'; publicBaseUrl = 'https://work.winomail.app' }
        discord = [ordered]@{
            webhooks = [ordered]@{ queue = ''; issues = ''; proposals = ''; alerts = ''; brief = '' }
            issuesChannelId = ''
        }
        limits = [ordered]@{ maxWorktrees = 10; maxPrototypes = 2; buildTimeoutMinutes = 60; answerTimeoutHours = 4 }
        defaults = [ordered]@{ model = 'claude-opus-5-5'; effort = 'medium' }
    }
}

function Get-Config {
    if ($null -eq $script:Config) {
        if (-not (Test-Path -LiteralPath $script:ConfigPath)) { throw "Missing $($script:ConfigPath). Run work.ps1 init first." }
        $script:Config = Read-JsonFile $script:ConfigPath
    }
    return $script:Config
}

function Get-LocalTime {
    param([DateTime]$Utc = [DateTime]::UtcNow)
    return [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::SpecifyKind($Utc, 'Utc'), (Get-Config).timeZone)
}

function Format-Stamp {
    param($Value)
    $utc = ConvertTo-UtcDate $Value
    if ($null -eq $utc) { return '-' }
    return (Get-LocalTime $utc).ToString('yyyy-MM-dd HH:mm', [Globalization.CultureInfo]::InvariantCulture)
}

function Test-QuietHours {
    $quiet = (Get-Config).quietHours
    $now = (Get-LocalTime).TimeOfDay
    $start = [TimeSpan]::Parse($quiet.start)
    $end = [TimeSpan]::Parse($quiet.end)
    if ($start -le $end) { return $now -ge $start -and $now -lt $end }
    return $now -ge $start -or $now -lt $end
}

function Invoke-Locked {
    param([scriptblock]$LockedBody)
    $lockMutex = [Threading.Mutex]::new($false, 'Global\WinoWork')
    $lockOwned = $false
    try {
        try { $lockOwned = $lockMutex.WaitOne([TimeSpan]::FromSeconds(60)) }
        catch [Threading.AbandonedMutexException] { $lockOwned = $true }
        if (-not $lockOwned) { throw 'Timed out waiting for the Global\WinoWork lock.' }
        & $LockedBody
    } finally {
        if ($lockOwned) { $lockMutex.ReleaseMutex() }
        $lockMutex.Dispose()
    }
}

function Read-StateFile {
    $state = if (Test-Path -LiteralPath $script:StatePath) { Read-JsonFile $script:StatePath } else { @{} }
    if ($null -eq $state) { $state = @{} }
    if (-not $state.Contains('issues') -or $null -eq $state.issues) { $state.issues = @{} }
    if (-not $state.Contains('pendingNotifications') -or $null -eq $state.pendingNotifications) { $state.pendingNotifications = @() }
    if (-not $state.Contains('mac') -or $null -eq $state.mac) { $state.mac = @{ reachable = $null; lastCheck = $null; lastChange = $null } }
    foreach ($key in @('lastTick', 'lastBoot', 'lastBrief', 'paused')) { if (-not $state.Contains($key)) { $state[$key] = $null } }
    $state.pendingNotifications = @($state.pendingNotifications)
    return $state
}

function Get-State { return Invoke-Locked { Read-StateFile } }

function Update-State {
    param([scriptblock]$StateChange)
    return Invoke-Locked {
        $lockedState = Read-StateFile
        $changeResult = & $StateChange $lockedState
        Write-TextFile $script:StatePath ($lockedState | ConvertTo-Json -Depth 10)
        $changeResult
    }
}

function Update-Issue {
    param([int]$Number, [hashtable]$Values)
    $null = Update-State {
        param($s)
        $record = $s.issues["$Number"]
        if ($null -eq $record) { return }
        foreach ($key in $Values.Keys) { $record[$key] = $Values[$key] }
        $record.updatedAt = Get-UtcStamp
    }
}

function Get-IssueRecord {
    param([int]$Number, $State = (Get-State))
    return $State.issues["$Number"]
}

function Get-IssueDir { param([int]$Number) return Join-Path $script:WorkRoot "issues\$Number" }
function Get-WorktreePath { param([int]$Number) return Join-Path $script:WorkRoot "wt\$Number" }

function ConvertTo-IssueNumber {
    param([string]$Value)
    $match = [regex]::Match([string]$Value, '^(?:#|.*/issues/)?(\d+)/?$')
    if (-not $match.Success) { throw "Expected an issue number, got '$Value'." }
    return [int]$match.Groups[1].Value
}

function Get-Argument {
    param([int]$Index, [string]$Description)
    if ($Arguments.Count -le $Index -or [string]::IsNullOrWhiteSpace($Arguments[$Index])) { throw "Missing $Description. Run work.ps1 help." }
    return $Arguments[$Index]
}

function Invoke-Git {
    param([string[]]$GitArguments, [switch]$AllowFailure)
    $output = & git -C (Get-Config).repoPath @GitArguments 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "git $($GitArguments -join ' ') failed ($LASTEXITCODE): $($output -join ' ')" }
    return @($output)
}

function Test-RemoteBranch {
    param([string]$Branch)
    return [bool](Invoke-Git @('ls-remote', '--heads', 'origin', $Branch) | Where-Object { $_ -match 'refs/heads/' })
}

function Invoke-Gh {
    param([string[]]$GhArguments)
    Push-Location -LiteralPath (Get-Config).repoPath
    try {
        $output = & gh @GhArguments 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { throw "gh $($GhArguments[0..1] -join ' ') failed ($LASTEXITCODE): $($output -join ' ')" }
        return $output -join "`n"
    } finally { Pop-Location }
}

function Test-WorkerAlive {
    param($ProcessId)
    if ($null -eq $ProcessId -or [int]$ProcessId -le 0) { return $false }
    $process = Get-Process -Id ([int]$ProcessId) -ErrorAction SilentlyContinue
    return $null -ne $process -and $process.ProcessName -eq 'cmd'
}

function Stop-ProcessTree {
    param([int]$ProcessId)
    $null = & taskkill.exe /PID $ProcessId /T /F 2>&1
}

function Get-DiscordBotToken {
    if ($env:WINO_WORK_DISCORD_BOT_TOKEN) { return $env:WINO_WORK_DISCORD_BOT_TOKEN }
    $file = Join-Path $HOME '.claude\channels\discord\.env'
    if (Test-Path -LiteralPath $file) {
        foreach ($line in Get-Content -LiteralPath $file) {
            if ($line -match '^\s*DISCORD_BOT_TOKEN\s*=\s*(.+?)\s*$') { return $Matches[1].Trim('"', "'") }
        }
    }
    return $null
}

function Get-WebhookUrl {
    param([string]$Channel)
    $url = (Get-Config).discord.webhooks[$Channel]
    if ([string]::IsNullOrWhiteSpace($url)) { throw "The Discord webhook for '$Channel' is not set in $($script:ConfigPath) (discord.webhooks.$Channel)." }
    return $url
}

function Split-Message {
    param([string]$Text, [int]$Limit = 1900)
    $parts = [Collections.Generic.List[string]]::new()
    $rest = $Text
    while ($rest.Length -gt $Limit) {
        $cut = $rest.LastIndexOf("`n", $Limit)
        if ($cut -lt $Limit / 2) { $cut = $Limit }
        $parts.Add($rest.Substring(0, $cut))
        $rest = $rest.Substring($cut).TrimStart("`n")
    }
    if ($rest.Length) { $parts.Add($rest) }
    return $parts.ToArray()
}

function Get-HttpStatus {
    param($ErrorRecord)
    $response = $ErrorRecord.Exception.PSObject.Properties['Response']?.Value
    if ($null -eq $response) { return 0 }
    return [int]$response.StatusCode
}

function Send-DiscordMessage {
    param([string]$Channel, [string]$Text, [string]$ThreadId)
    $url = Get-WebhookUrl $Channel
    if ($ThreadId) { $url += "?thread_id=$ThreadId" }
    foreach ($part in Split-Message $Text) {
        $body = @{ content = $part; allowed_mentions = @{ parse = @() } } | ConvertTo-Json -Compress -Depth 4
        for ($attempt = 1; ; $attempt++) {
            try {
                $null = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body $body
                break
            } catch {
                $status = Get-HttpStatus $_
                if ($status -eq 429 -and $attempt -lt 3) { Start-Sleep -Seconds 3; continue }
                throw "Discord post to the '$Channel' channel failed (HTTP $status)."
            }
        }
    }
}

function Send-Notification {
    param([string]$Channel, [string]$Text, $Issue, [switch]$NoQueue)
    $null = Get-WebhookUrl $Channel
    if (-not $NoQueue -and (Test-QuietHours)) {
        $null = Update-State {
            param($s)
            $s.pendingNotifications = @($s.pendingNotifications) + @([ordered]@{ channel = $Channel; text = $Text; issue = $Issue; queuedAt = (Get-UtcStamp) })
        }
        return 'queued'
    }
    $threadId = $null
    if ($Channel -eq 'issues' -and $null -ne $Issue) { $threadId = (Get-IssueRecord $Issue)?.threadId }
    Send-DiscordMessage $Channel $Text $threadId
    return 'sent'
}

function Send-Alert {
    param([string]$Text, [switch]$Critical)
    return Send-Notification 'alerts' $Text $null -NoQueue:$Critical
}

function Send-QuietAlert {
    param([string]$Text, [switch]$Critical)
    try { $null = Send-Alert $Text -Critical:$Critical } catch { Write-Warning "Alert not posted: $($_.Exception.Message) Alert text: $Text" }
}

function New-DiscordThread {
    param([string]$ThreadName)
    $token = Get-DiscordBotToken
    $channelId = (Get-Config).discord.issuesChannelId
    if (-not $token -or [string]::IsNullOrWhiteSpace($channelId)) {
        Write-Warning 'No Discord thread created: set WINO_WORK_DISCORD_BOT_TOKEN (or the channel plugin .env) and discord.issuesChannelId.'
        return $null
    }
    $body = @{ name = $ThreadName.Substring(0, [Math]::Min(100, $ThreadName.Length)); type = 11; auto_archive_duration = 10080 } | ConvertTo-Json -Compress
    try {
        $thread = Invoke-RestMethod -Method Post -Uri "https://discord.com/api/v10/channels/$channelId/threads" -ContentType 'application/json' -Body $body -Headers @{ Authorization = "Bot $token"; 'User-Agent' = 'DiscordBot (https://winomail.app, 1.0)' }
        return [string]$thread.id
    } catch {
        Write-Warning "No Discord thread created (HTTP $(Get-HttpStatus $_))."
        return $null
    }
}

function Get-R2Credentials {
    $values = @{}
    foreach ($variable in @('WINO_R2_ACCOUNT_ID', 'WINO_R2_ACCESS_KEY_ID', 'WINO_R2_SECRET_ACCESS_KEY')) {
        $values[$variable] = [Environment]::GetEnvironmentVariable($variable)
        if ([string]::IsNullOrWhiteSpace($values[$variable])) { throw "Set $variable before publishing." }
    }
    return [pscustomobject]@{
        Host = "$($values.WINO_R2_ACCOUNT_ID).r2.cloudflarestorage.com"
        AccessKeyId = $values.WINO_R2_ACCESS_KEY_ID
        SecretAccessKey = $values.WINO_R2_SECRET_ACCESS_KEY
    }
}

function Get-HexHash { param([byte[]]$Bytes) return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function Get-Hmac { param([byte[]]$Key, [string]$Data) return [Security.Cryptography.HMACSHA256]::HashData($Key, [Text.Encoding]::UTF8.GetBytes($Data)) }

function Send-R2Object {
    param([object]$Credentials, [string]$Key, [byte[]]$Body, [string]$ContentType, [string]$CacheControl)
    $bucket = (Get-Config).r2.bucket
    $path = "/$bucket/" + (($Key.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    $now = [DateTime]::UtcNow
    $amzDate = $now.ToString("yyyyMMdd'T'HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $date = $now.ToString('yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
    $payloadHash = Get-HexHash $Body
    $signedHeaders = 'host;x-amz-content-sha256;x-amz-date'
    $canonical = "PUT`n$path`n`nhost:$($Credentials.Host)`nx-amz-content-sha256:$payloadHash`nx-amz-date:$amzDate`n`n$signedHeaders`n$payloadHash"
    $scope = "$date/auto/s3/aws4_request"
    $stringToSign = "AWS4-HMAC-SHA256`n$amzDate`n$scope`n$(Get-HexHash ([Text.Encoding]::UTF8.GetBytes($canonical)))"
    $signingKey = [Text.Encoding]::UTF8.GetBytes("AWS4$($Credentials.SecretAccessKey)")
    foreach ($part in @($date, 'auto', 's3', 'aws4_request')) { $signingKey = Get-Hmac $signingKey $part }
    $signature = [Convert]::ToHexString((Get-Hmac $signingKey $stringToSign)).ToLowerInvariant()

    if ($null -eq $script:Http) { $script:Http = [Net.Http.HttpClient]::new(); $script:Http.Timeout = [TimeSpan]::FromMinutes(30) }
    $request = [Net.Http.HttpRequestMessage]::new('PUT', "https://$($Credentials.Host)$path")
    $null = $request.Headers.TryAddWithoutValidation('x-amz-date', $amzDate)
    $null = $request.Headers.TryAddWithoutValidation('x-amz-content-sha256', $payloadHash)
    $null = $request.Headers.TryAddWithoutValidation('Authorization', "AWS4-HMAC-SHA256 Credential=$($Credentials.AccessKeyId)/$scope, SignedHeaders=$signedHeaders, Signature=$signature")
    $null = $request.Headers.TryAddWithoutValidation('Cache-Control', $CacheControl)
    $request.Content = [Net.Http.ByteArrayContent]::new($Body)
    $null = $request.Content.Headers.TryAddWithoutValidation('Content-Type', $ContentType)
    $response = $script:Http.Send($request)
    if ($response.IsSuccessStatusCode) { return }
    $status = [int]$response.StatusCode
    if ($status -eq 403) { throw "R2 refused the upload of $Key with 403: the R2 keys in WINO_R2_* have no write access to bucket '$bucket', or the bucket does not exist." }
    if ($status -eq 404) { throw "R2 returned 404 for ${Key}: bucket '$bucket' does not exist." }
    throw "R2 upload of $Key to bucket '$bucket' failed with HTTP $status."
}

function Get-ContentType {
    param([string]$FileName)
    $type = $script:ContentTypes[[IO.Path]::GetExtension($FileName).ToLowerInvariant()]
    if (-not $type) { throw "Cannot publish '$FileName': use .md, .html, .png, .jpg, .gif, .svg, .mp4, .webm, .json, or .txt." }
    return $type
}

function ConvertTo-HtmlText { param($Value) return [Net.WebUtility]::HtmlEncode([string]$Value) }

function New-SitePage {
    param([string]$Title, [string]$BodyHtml)
    $css = Get-Content -LiteralPath (Join-Path $script:TemplateRoot 'site.css') -Raw
    return @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>$(ConvertTo-HtmlText $Title)</title>
<style>
$css
</style>
</head>
<body>
<main>
$BodyHtml
</main>
</body>
</html>
"@
}

function Get-IssueTitle {
    param([int]$Number)
    $record = Get-IssueRecord $Number
    if ($record -and $record.title) { return $record.title }
    $issueFile = Join-Path (Get-IssueDir $Number) 'issue.json'
    if (Test-Path -LiteralPath $issueFile) { return (Read-JsonFile $issueFile).title }
    return "Issue $Number"
}

function Get-PublicUrl { param([string]$Key) return "$((Get-Config).r2.publicBaseUrl.TrimEnd('/'))/$Key" }

function Publish-Artifact {
    param([int]$Number, [string]$Path, [string]$Kind, [string]$PublishName)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "File not found: $Path" }
    $source = (Resolve-Path -LiteralPath $Path).Path
    $extension = [IO.Path]::GetExtension($source).ToLowerInvariant()
    if (-not $Kind) { $Kind = switch -Regex ($extension) { '^\.(png|jpe?g|gif)$' { 'screen' } '^\.(mp4|webm)$' { 'video' } default { 'other' } } }
    $fileName = if ($PublishName) { $PublishName } else { Split-Path -Leaf $source }
    if ($extension -eq '.md') { $fileName = [IO.Path]::ChangeExtension($fileName, '.html') }
    if ($fileName -match '[\\/]|^\.\.?$') { throw "Invalid published name '$fileName'." }
    $relative = "$($script:KindFolders[$Kind])$fileName"
    $key = "$Number/$relative"
    $title = "#$Number $(Get-IssueTitle $Number)"
    $siteDir = Join-Path (Get-IssueDir $Number) 'site'

    if ($extension -eq '.md') {
        $depth = ($relative.Split('/').Count - 1)
        $nav = "<p class=`"nav`"><a href=`"$('../' * $depth)index.html`">$(ConvertTo-HtmlText $title)</a></p>"
        $html = New-SitePage $title ($nav + "`n" + (ConvertFrom-Markdown -LiteralPath $source).Html)
        $staged = Join-Path $siteDir ($relative -replace '/', '\')
        Write-TextFile $staged $html
        $source = $staged
    }
    $contentType = Get-ContentType $fileName
    $manifest = Update-Manifest $Number $Kind $key $relative (Get-Item -LiteralPath $source).Length
    Update-IssueAfterPublish $Number $Kind
    $pages = @(
        @{ Key = "$Number/index.html"; Stage = 'index.html'; Html = (New-IssuePage $Number $manifest) }
        @{ Key = 'index.html'; Stage = 'site-root\index.html'; Html = (New-RootPage) }
    )
    foreach ($page in $pages) { Write-TextFile (Join-Path $siteDir $page.Stage) $page.Html }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6

    $credentials = Get-R2Credentials
    Send-R2Object $credentials $key ([IO.File]::ReadAllBytes($source)) $contentType (Get-CacheControl $contentType)
    Send-R2Object $credentials "$Number/manifest.json" $script:Utf8.GetBytes($manifestJson) $script:ContentTypes['.json'] 'no-cache'
    foreach ($page in $pages) { Send-R2Object $credentials $page.Key $script:Utf8.GetBytes($page.Html) $script:ContentTypes['.html'] 'no-cache' }
    return Get-PublicUrl $key
}

function Get-CacheControl {
    param([string]$ContentType)
    if ($ContentType -match '^(text/html|application/json)') { return 'no-cache' }
    return 'public, max-age=300'
}

function Update-Manifest {
    param([int]$Number, [string]$Kind, [string]$Key, [string]$Relative, [long]$Size)
    $file = Join-Path (Get-IssueDir $Number) 'manifest.json'
    return Invoke-Locked {
        $manifest = if (Test-Path -LiteralPath $file) { Read-JsonFile $file } else { [ordered]@{ number = $Number; artifacts = @() } }
        $entries = @($manifest.artifacts | Where-Object { $_.key -ne $Key })
        $entries += [ordered]@{ kind = $Kind; key = $Key; path = $Relative; size = $Size; publishedAt = (Get-UtcStamp) }
        $manifest.title = Get-IssueTitle $Number
        $manifest.status = (Get-IssueRecord $Number)?.status
        $manifest.updatedAt = Get-UtcStamp
        $manifest.artifacts = $entries
        Write-TextFile $file ($manifest | ConvertTo-Json -Depth 6)
        $manifest
    }
}

function Update-IssueAfterPublish {
    param([int]$Number, [string]$Kind)
    $record = Get-IssueRecord $Number
    if ($null -eq $record) { return }
    $values = @{}
    if ($record.status -in $script:ActiveStatuses) {
        switch ($Kind) {
            'plan' { $values = @{ status = 'implementing'; step = 'plan published' } }
            'design' { $values = @{ step = 'design published' } }
            { $_ -in 'screen', 'video' } { $values = @{ status = 'verifying'; step = 'verification evidence published' } }
            'report' { $values = @{ step = 'report published' } }
        }
    }
    Update-Issue $Number $values
}

function New-IssuePage {
    param([int]$Number, $Manifest)
    $record = Get-IssueRecord $Number
    $title = "#$Number $(Get-IssueTitle $Number)"
    $issueFile = Join-Path (Get-IssueDir $Number) 'issue.json'
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('<p class="nav"><a href="../index.html">Wino work</a></p>')
    $lines.Add("<h1>$(ConvertTo-HtmlText $title)</h1>")
    $facts = @("Status: <strong>$(ConvertTo-HtmlText (${record}?.status ?? 'not tracked'))</strong>")
    if (${record}?.step) { $facts += "Step: $(ConvertTo-HtmlText $record.step)" }
    if (${record}?.branch) { $facts += "Branch: <code>$(ConvertTo-HtmlText $record.branch)</code>" }
    if (${record}?.parity) { $facts += "Parity: $(ConvertTo-HtmlText $record.parity)" }
    $facts += "Updated: $(Format-Stamp $Manifest.updatedAt)"
    $lines.Add("<p class=`"meta`">$($facts -join ' &middot; ')</p>")
    if (Test-Path -LiteralPath $issueFile) { $lines.Add("<p><a href=`"$(ConvertTo-HtmlText (Read-JsonFile $issueFile).url)`">GitHub issue</a></p>") }
    foreach ($kind in $script:KindTitles.Keys) {
        $items = @($Manifest.artifacts | Where-Object { $_.kind -eq $kind } | Sort-Object { $_.path })
        if (-not $items.Count) { continue }
        $lines.Add("<h2>$($script:KindTitles[$kind])</h2>")
        foreach ($item in $items) {
            $href = ConvertTo-HtmlText (($item.path.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
            $label = ConvertTo-HtmlText $item.path
            $stamp = Format-Stamp $item.publishedAt
            if ($kind -eq 'screen' -and $item.path -notmatch '\.html$') {
                $lines.Add("<figure><a href=`"$href`"><img src=`"$href`" alt=`"$label`" loading=`"lazy`"></a><figcaption>$label &middot; $stamp</figcaption></figure>")
            } elseif ($kind -eq 'video') {
                $lines.Add("<figure><video src=`"$href`" controls preload=`"metadata`"></video><figcaption><a href=`"$href`">$label</a> &middot; $stamp</figcaption></figure>")
            } else {
                $lines.Add("<ul class=`"artifacts`"><li><a href=`"$href`">$label</a> <span class=`"meta`">$stamp</span></li></ul>")
            }
        }
    }
    return New-SitePage $title ($lines -join "`n")
}

function New-RootPage {
    $state = Get-State
    $rows = foreach ($record in ($state.issues.Values | Sort-Object { [int]$_.number } -Descending)) {
        "<tr><td><a href=`"$($record.number)/index.html`">#$($record.number)</a></td><td>$(ConvertTo-HtmlText $record.title)</td><td>$(ConvertTo-HtmlText $record.status)</td><td>$(Format-Stamp $record.updatedAt)</td></tr>"
    }
    $table = if ($rows) { "<div class=`"table-wrap`"><table><thead><tr><th>Issue</th><th>Title</th><th>Status</th><th>Updated</th></tr></thead><tbody>`n$($rows -join "`n")`n</tbody></table></div>" } else { '<p>No issues yet.</p>' }
    return New-SitePage 'Wino work' "<h1>Wino work</h1>`n<p class=`"meta`">Updated $(Format-Stamp (Get-UtcStamp))</p>`n$table"
}

function Get-Slug {
    param([string]$Title)
    $ascii = [Text.Encoding]::ASCII.GetString([Text.Encoding]::GetEncoding('ASCII', [Text.EncoderReplacementFallback]::new(''), [Text.DecoderReplacementFallback]::new('')).GetBytes($Title.Normalize([Text.NormalizationForm]::FormD)))
    $slug = ($ascii.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).TrimEnd('-') }
    if (-not $slug) { $slug = 'issue' }
    return $slug
}

function Format-IssueComments {
    param($Issue)
    $comments = @($Issue.comments)
    if (-not $comments.Count) { return 'No comments.' }
    return ($comments | ForEach-Object { "**$($_.author.login)** ($($_.createdAt)):`n`n$($_.body)" }) -join "`n`n---`n`n"
}

function Get-DesignText {
    param([int]$Count)
    if ($Count -le 0) { return 'No design was requested.' }
    if ($Count -eq 1) { return 'The owner asked for one design prototype before implementation.' }
    $letters = (0..($Count - 1) | ForEach-Object { [char](65 + $_) }) -join ', '
    return "The owner asked for $Count design alternatives ($letters) before implementation and picks one."
}

function New-Worktree {
    param([int]$Number, [string]$Branch)
    $worktree = Get-WorktreePath $Number
    if (Test-Path -LiteralPath (Join-Path $worktree '.git')) { return 'reused' }
    $count = @(Get-ChildItem -LiteralPath (Join-Path $script:WorkRoot 'wt') -Directory -ErrorAction SilentlyContinue).Count
    if ($count -ge (Get-Config).limits.maxWorktrees) { throw "The worktree cap ($((Get-Config).limits.maxWorktrees)) is reached. Finish or stop an issue first." }
    $null = Invoke-Git @('worktree', 'prune')
    $null = Invoke-Git @('show-ref', '--verify', '--quiet', "refs/heads/$Branch") -AllowFailure
    if ($LASTEXITCODE -eq 0) { $null = Invoke-Git @('worktree', 'add', $worktree, $Branch); return 'local branch' }
    if (Test-RemoteBranch $Branch) { $null = Invoke-Git @('worktree', 'add', '--track', '-b', $Branch, $worktree, "origin/$Branch"); return 'origin branch' }
    $null = Invoke-Git @('worktree', 'add', '--no-track', '-b', $Branch, $worktree, 'origin/main')
    return 'new branch from origin/main'
}

function Move-Aside {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $Path -Destination ($Path -replace '(\.\w+)$', "-$stamp`$1") -Force
}

function Start-Worker {
    param([int]$Number, [string]$WorkerModel, [string]$WorkerEffort)
    $issueDir = Get-IssueDir $Number
    $log = Join-Path $issueDir 'worker.log'
    Move-Aside $log
    $commandLine = "claude -p --dangerously-skip-permissions --model $WorkerModel --effort $WorkerEffort < `"$(Join-Path $issueDir 'brief.md')`" > `"$log`" 2>&1"
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList "/d /s /c `"$commandLine`"" -WorkingDirectory (Get-WorktreePath $Number) -WindowStyle Hidden -PassThru
    return $process.Id
}

function Invoke-Start {
    $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
    $config = Get-Config
    $existing = Get-IssueRecord $number
    if ($existing -and (Test-WorkerAlive $existing.pid)) { throw "A worker for #$number is already running (pid $($existing.pid)). Stop it first." }
    $workerModel = if ($Model) { $Model } else { $config.defaults.model }
    $workerEffort = if ($Effort) { $Effort } else { $config.defaults.effort }
    if ($workerModel -notmatch '^[A-Za-z0-9._\-\[\]]+$') { throw "Invalid model name '$workerModel'." }
    if ($workerEffort -notin @('low', 'medium', 'high', 'xhigh', 'max')) { throw "Invalid effort '$workerEffort'." }

    $issueDir = Get-IssueDir $number
    $null = New-Item -ItemType Directory -Path $issueDir, (Join-Path $issueDir 'questions'), (Join-Path $issueDir 'answers') -Force
    $issueJson = Invoke-Gh @('issue', 'view', "$number", '--json', 'number,title,body,labels,comments,url')
    Write-TextFile (Join-Path $issueDir 'issue.json') $issueJson
    $issue = $issueJson | ConvertFrom-Json -AsHashtable -DateKind String
    $null = Invoke-Gh @('label', 'create', 'queued', '--color', '5319E7', '--description', 'An agent is working on it', '--force')
    $null = Invoke-Gh @('issue', 'edit', "$number", '--add-label', 'queued')

    $branch = if ($existing -and $existing.branch) { $existing.branch } else { "agent/$number-$(Get-Slug $issue.title)" }
    $null = Invoke-Git @('fetch', 'origin', '--prune')
    $how = New-Worktree $number $branch
    $worktree = Get-WorktreePath $number
    $siteUrl = Get-PublicUrl "$number/index.html"

    $values = @{
        number = "$number"; title = $issue.title; url = $issue.url; body = ($issue.body ?? '').Trim()
        comments = Format-IssueComments $issue
        instructions = if ($Instructions) { $Instructions } else { 'None. Fix the issue as described.' }
        design = Get-DesignText $Design; workRoot = $script:WorkRoot; worktree = $worktree; branch = $branch
        siteUrl = $siteUrl; script = $PSCommandPath
    }
    $template = Get-Content -LiteralPath (Join-Path $script:TemplateRoot 'worker-brief.md') -Raw
    $brief = [regex]::Replace($template, '\{\{(\w+)\}\}', { param($m) if ($values.ContainsKey($m.Groups[1].Value)) { $values[$m.Groups[1].Value] } else { $m.Value } })
    Write-TextFile (Join-Path $issueDir 'brief.md') $brief
    Move-Aside (Join-Path $issueDir 'report.md')

    $threadId = ${existing}?.threadId
    if (-not $threadId) { $threadId = New-DiscordThread "#$number $($issue.title)" }
    $now = Get-UtcStamp
    $null = Update-State {
        param($s)
        $s.issues["$number"] = [ordered]@{
            number = $number; title = $issue.title; slug = Get-Slug $issue.title; branch = $branch; worktree = $worktree
            status = 'queued'; step = 'worktree ready'; model = $workerModel; effort = $workerEffort; design = $Design
            instructions = $Instructions; pid = $null; startedAt = $now; updatedAt = $now; threadId = $threadId
            parity = 'pending'; lastError = $null
        }
    }
    Write-Output "Worktree $worktree ($how), branch $branch."
    if (-not $NoLaunch) {
        $workerPid = Start-Worker $number $workerModel $workerEffort
        Update-Issue $number @{ status = 'planning'; step = 'worker launched'; pid = $workerPid }
        Write-Output "Worker launched (pid $workerPid, $workerModel, effort $workerEffort). Log: $(Join-Path $issueDir 'worker.log')"
    } else {
        Write-Output 'Worker not launched (-NoLaunch).'
    }
    try {
        $result = Send-Notification 'issues' "Started #${number}: $($issue.title). Branch $branch. Site: $siteUrl" $number
        Write-Output "Start message $result."
    } catch { Write-Warning $_.Exception.Message }
}

function Invoke-Stop {
    $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
    $record = Get-IssueRecord $number
    if ($null -eq $record) { throw "Issue #$number is not tracked." }
    if (Test-WorkerAlive $record.pid) { Stop-ProcessTree $record.pid; Write-Output "Stopped worker pid $($record.pid)." } else { Write-Output 'No live worker.' }
    Update-Issue $number @{ status = 'paused'; step = 'stopped by owner'; pid = $null }
    try { $null = Send-Notification 'issues' "Stopped #${number}." $number } catch { Write-Warning $_.Exception.Message }
}

function Invoke-Ask {
    $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
    $text = Get-Argument 1 'question text'
    $hours = if ($TimeoutHours -gt 0) { $TimeoutHours } else { [double](Get-Config).limits.answerTimeoutHours }
    $issueDir = Get-IssueDir $number
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $answerFile = Join-Path $issueDir 'answer.md'
    if (Test-Path -LiteralPath $answerFile) { Move-Item -LiteralPath $answerFile -Destination (Join-Path $issueDir "answers\stale-$stamp.md") -Force }
    Write-TextFile (Join-Path $issueDir "questions\$stamp.md") $text
    try { $null = Send-Notification 'issues' "Question on #${number}: $text" $number } catch { Write-Warning "Question not posted: $($_.Exception.Message) It is still saved in the questions folder." }
    $previousStep = (Get-IssueRecord $number)?.step
    Update-Issue $number @{ step = 'waiting for the owner' }
    $deadline = [DateTime]::UtcNow.AddHours($hours)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $answerFile) {
            $answer = Get-Content -LiteralPath $answerFile -Raw
            Write-TextFile (Join-Path $issueDir "answers\$stamp.md") $answer
            Remove-Item -LiteralPath $answerFile -Force
            Update-Issue $number @{ step = $previousStep }
            Write-Output $answer
            return
        }
        Start-Sleep -Seconds 20
    }
    Update-Issue $number @{ step = $previousStep }
    Write-Output "No answer within $hours hours. Continue with your best assumption and state it in the report."
    $script:ExitCode = 2
}

function Invoke-Answer {
    $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
    $text = Get-Argument 1 'answer text'
    Write-TextFile (Join-Path (Get-IssueDir $number) 'answer.md') $text
    Write-Output "Answer saved for #$number."
}

function Get-LogTail {
    param([int]$Number, [int]$Lines = 15)
    $log = Join-Path (Get-IssueDir $Number) 'worker.log'
    if (-not (Test-Path -LiteralPath $log)) { return '(no worker.log)' }
    return (Get-Content -LiteralPath $log -Tail $Lines) -join "`n"
}

function Invoke-Reap {
    foreach ($record in @((Get-State).issues.Values | Where-Object { $_.pid })) {
        if (Test-WorkerAlive $record.pid) { continue }
        $number = [int]$record.number
        if (Test-Path -LiteralPath (Join-Path (Get-IssueDir $number) 'report.md')) {
            Update-Issue $number @{ status = 'finished'; step = 'report written'; pid = $null; lastError = $null }
            $message = "Finished #${number}: $($record.title). Report: $(Get-PublicUrl "$number/report.html")"
        } else {
            $tail = Get-LogTail $number 8
            $lastError = if ($tail.Length -gt 1500) { $tail.Substring($tail.Length - 1500) } else { $tail }
            Update-Issue $number @{ status = 'failed'; step = 'worker exited without a report'; pid = $null; lastError = $lastError }
            $message = "Worker for #$number exited without a report. Last log lines:`n``````n$lastError`n``````"
        }
        Write-Output $message.Split("`n")[0]
        try { $null = Send-Notification 'issues' $message $number } catch { Write-Warning $_.Exception.Message }
    }
}

function Invoke-BuildTimeouts {
    $limit = [int](Get-Config).limits.buildTimeoutMinutes
    $issues = @((Get-State).issues.Values | Where-Object { $_.worktree })
    if (-not $issues.Count) { return }
    $processes = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='MSBuild.exe' OR Name='winapp.exe'"
    foreach ($process in $processes) {
        if (-not $process.CommandLine -or $process.CreationDate -gt (Get-Date).AddMinutes(-$limit)) { continue }
        $owner = $issues | Where-Object { $process.CommandLine -match ([regex]::Escape($_.worktree) + '(?=[\\/"\s]|$)') } | Select-Object -First 1
        if (-not $owner) { continue }
        Stop-ProcessTree $process.ProcessId
        $minutes = [int]((Get-Date) - $process.CreationDate).TotalMinutes
        Update-Issue ([int]$owner.number) @{ status = 'failed'; lastError = "$($process.Name) ran $minutes minutes and was killed (cap $limit)." }
        Write-Output "Killed $($process.Name) pid $($process.ProcessId) of #$($owner.number) after $minutes minutes."
        Send-QuietAlert "Build timeout on #$($owner.number): $($process.Name) ran $minutes minutes (cap $limit) and was killed."
    }
}

function Invoke-Prune {
    foreach ($record in @((Get-State).issues.Values | Where-Object { $_.status -in 'finished', 'failed' -and $_.worktree })) {
        if ((Test-WorkerAlive $record.pid) -or -not (Test-Path -LiteralPath $record.worktree)) { continue }
        if (-not (Test-RemoteBranch $record.branch)) { continue }
        $null = Invoke-Git @('worktree', 'remove', '--force', $record.worktree)
        Write-Output "Removed worktree of #$($record.number) ($($record.branch) is on origin)."
    }
    $null = Invoke-Git @('worktree', 'prune')
}

function Test-Mac {
    $config = Get-Config
    $output = & $config.sshExe -o ConnectTimeout=8 -o BatchMode=yes $config.macHost 'echo ok' 2>$null
    $reachable = $LASTEXITCODE -eq 0 -and (@($output) -contains 'ok')
    $previous = Update-State {
        param($s)
        $before = $s.mac.reachable
        $s.mac.reachable = $reachable
        $s.mac.lastCheck = Get-UtcStamp
        if ($before -ne $reachable) { $s.mac.lastChange = Get-UtcStamp }
        $before
    }
    if ($null -ne $previous -and $previous -ne $reachable) {
        Send-QuietAlert $(if ($reachable) { "The Mac ($($config.macHost)) is reachable again." } else { "The Mac ($($config.macHost)) is unreachable over SSH. Parity work is skipped until it is back." })
    }
    return $reachable
}

function Send-PendingNotifications {
    $pending = @((Get-State).pendingNotifications)
    if (-not $pending.Count -or (Test-QuietHours)) { return }
    $sent = [Collections.Generic.List[string]]::new()
    foreach ($item in $pending) {
        try {
            $threadId = if ($item.channel -eq 'issues' -and $null -ne $item.issue) { (Get-IssueRecord $item.issue)?.threadId } else { $null }
            Send-DiscordMessage $item.channel $item.text $threadId
            $sent.Add("$($item.queuedAt)|$($item.channel)|$($item.text)")
        } catch { Write-Warning "Queued post not sent yet: $($_.Exception.Message)" }
    }
    $null = Update-State { param($s) $s.pendingNotifications = @($s.pendingNotifications | Where-Object { "$($_.queuedAt)|$($_.channel)|$($_.text)" -notin $sent }) }
    Write-Output "Sent $($sent.Count) of $($pending.Count) queued posts."
}

function Start-IdlePrototypes { }

function Invoke-Tick {
    $steps = [ordered]@{
        reap = { Invoke-Reap }
        'build timeout' = { Invoke-BuildTimeouts }
        prune = { Invoke-Prune }
        'mac ping' = { $null = Test-Mac }
        prototypes = { if (-not (Get-State).paused) { Start-IdlePrototypes } }
        flush = { Send-PendingNotifications }
    }
    foreach ($step in $steps.Keys) {
        try { & $steps[$step] } catch { Write-Warning "tick $step failed: $($_.Exception.Message)" }
    }
    $null = Update-State { param($s) $s.lastTick = Get-UtcStamp }
    Write-Output "Tick done at $(Format-Stamp (Get-UtcStamp))."
}

function Get-LastBoot { return (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime() }

function Invoke-Boot {
    $boot = Get-LastBoot
    $state = Get-State
    $known = ConvertTo-UtcDate $state.lastBoot
    if ($null -ne $known -and [Math]::Abs(($boot - $known).TotalSeconds) -lt 5) { Write-Output 'No reboot since the last check.'; return }
    $interrupted = @($state.issues.Values | Where-Object { $_.pid })
    $null = Update-State {
        param($s)
        foreach ($record in $interrupted) {
            $s.issues["$($record.number)"].status = 'paused'
            $s.issues["$($record.number)"].step = 'interrupted by reboot'
            $s.issues["$($record.number)"].pid = $null
            $s.issues["$($record.number)"].updatedAt = Get-UtcStamp
        }
        $s.lastBoot = $boot.ToString('o')
    }
    if ($null -eq $known) { Write-Output "Recorded boot time $(Format-Stamp $boot.ToString('o'))."; return }
    $list = if ($interrupted.Count) { 'Interrupted workers, now paused: ' + (($interrupted | ForEach-Object { "#$($_.number) $($_.title) ($($_.step))" }) -join '; ') + '.' } else { 'No workers were running.' }
    $text = "The Windows machine restarted at $(Format-Stamp $boot.ToString('o')). $list"
    Write-Output $text
    Send-QuietAlert $text -Critical
}

function Get-UnansweredQuestions {
    param($Record)
    $issueDir = Get-IssueDir ([int]$Record.number)
    $cutoff = [DateTime]::UtcNow.AddMinutes(-20)
    return @(Get-ChildItem -LiteralPath (Join-Path $issueDir 'questions') -Filter '*.md' -ErrorAction SilentlyContinue | Where-Object {
        $_.LastWriteTimeUtc -lt $cutoff -and -not (Test-Path -LiteralPath (Join-Path $issueDir "answers\$($_.Name)"))
    })
}

function Invoke-Brief {
    $state = Get-State
    $since = ConvertTo-UtcDate $state.lastBrief
    if ($null -eq $since) { $since = [DateTime]::UtcNow.AddDays(-1) }
    $issues = @($state.issues.Values | Sort-Object { [int]$_.number })
    $label = { param($r) "#$($r.number) $($r.title)" }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("**Morning brief, $((Get-LocalTime).ToString('dddd d MMMM', [Globalization.CultureInfo]::InvariantCulture))**")

    $finished = @($issues | Where-Object { $_.status -eq 'finished' -and (ConvertTo-UtcDate $_.updatedAt) -gt $since })
    $lines.Add($(if ($finished.Count) { 'Finished since the last brief: ' + (($finished | ForEach-Object { "$(& $label $_) ($(Get-PublicUrl "$($_.number)/report.html"))" }) -join '; ') + '.' } else { 'Nothing finished since the last brief.' }))

    $questions = @($issues | Where-Object { $_.status -in $script:ActiveStatuses -and (Get-UnansweredQuestions $_).Count })
    $unmerged = @($issues | Where-Object { $_.status -eq 'finished' })
    if ($questions.Count) { $lines.Add('Waiting for your answer: ' + (($questions | ForEach-Object { & $label $_ }) -join '; ') + '.') }
    if ($unmerged.Count) { $lines.Add('Finished and waiting for merge: ' + (($unmerged | ForEach-Object { "#$($_.number)" }) -join ', ') + '.') }
    if (-not $questions.Count -and -not $unmerged.Count) { $lines.Add('Nothing is waiting for you.') }

    $stuck = @($issues | Where-Object { $_.status -in 'failed', 'paused' })
    if ($stuck.Count) { $lines.Add('Stuck: ' + (($stuck | ForEach-Object { "$(& $label $_) is $($_.status) ($(if ($_.lastError) { ($_.lastError -split "`n")[-1] } else { $_.step }))" }) -join '; ') + '.') }
    $skipped = @($issues | Where-Object { $_.parity -eq 'skipped' })
    if ($skipped.Count) { $lines.Add('Parity skipped: ' + (($skipped | ForEach-Object { "#$($_.number)" }) -join ', ') + '.') }

    $mac = $state.mac
    $lines.Add($(if ($null -eq $mac.reachable) { 'The Mac has not been checked yet.' } elseif ($mac.reachable) { "The Mac is reachable (checked $(Format-Stamp $mac.lastCheck))." } else { "The Mac is unreachable since $(Format-Stamp $mac.lastChange)." }))
    $boot = Get-LastBoot
    $uptime = [DateTime]::UtcNow - $boot
    $rebooted = if ($boot -gt $since) { ' It restarted since the last brief.' } else { '' }
    $lines.Add("Windows has been up $([int]$uptime.TotalDays) days $($uptime.Hours) hours.$rebooted")
    $pendingCount = @($state.pendingNotifications).Count
    if ($pendingCount) { $lines.Add("$pendingCount posts are queued from quiet hours.") }
    if ($state.paused) { $lines.Add('Idle prototyping is paused.') }

    $text = $lines -join "`n"
    Write-Output $text
    Send-DiscordMessage 'brief' $text $null
    $null = Update-State { param($s) $s.lastBrief = Get-UtcStamp }
}

function Invoke-Status {
    $state = Get-State
    $rows = foreach ($record in ($state.issues.Values | Sort-Object { [int]$_.number })) {
        [pscustomobject]@{
            Issue = "#$($record.number)"; Status = $record.status; Step = $record.step
            Worktree = [bool]($record.worktree -and (Test-Path -LiteralPath $record.worktree))
            Worker = if ($record.pid) { "$($record.pid) $(if (Test-WorkerAlive $record.pid) { 'alive' } else { 'gone' })" } else { '-' }
            Parity = $record.parity; Updated = Format-Stamp $record.updatedAt; Title = $record.title
        }
    }
    if ($rows) { Write-Output ($rows | Format-Table -AutoSize | Out-String).TrimEnd() } else { Write-Output 'No issues tracked.' }
    $worktrees = @(Get-ChildItem -LiteralPath (Join-Path $script:WorkRoot 'wt') -Directory -ErrorAction SilentlyContinue).Count
    $macText = if ($null -eq $state.mac.reachable) { 'not checked' } elseif ($state.mac.reachable) { "reachable (checked $(Format-Stamp $state.mac.lastCheck))" } else { "unreachable since $(Format-Stamp $state.mac.lastChange)" }
    Write-Output ''
    Write-Output "Worktrees: $worktrees of $((Get-Config).limits.maxWorktrees)"
    Write-Output "Mac: $macText"
    Write-Output "Queued posts: $(@($state.pendingNotifications).Count)"
    Write-Output "Prototyping: $(if ($state.paused) { 'paused' } else { 'allowed' })"
    Write-Output "Last tick: $(Format-Stamp $state.lastTick)"
}

function Invoke-Init {
    foreach ($folder in @($script:WorkRoot, (Join-Path $script:WorkRoot 'issues'), (Join-Path $script:WorkRoot 'wt'))) {
        if (-not (Test-Path -LiteralPath $folder)) { $null = New-Item -ItemType Directory -Path $folder; Write-Output "Created $folder" }
    }
    if (Test-Path -LiteralPath $script:ConfigPath) { Write-Output "Kept existing $($script:ConfigPath)" }
    else { Write-TextFile $script:ConfigPath ((Get-DefaultConfig) | ConvertTo-Json -Depth 6); Write-Output "Created $($script:ConfigPath)" }
    if (-not (Test-Path -LiteralPath $script:StatePath)) { $null = Update-State { param($s) }; Write-Output "Created $($script:StatePath)" }

    $config = Get-Config
    $todo = [Collections.Generic.List[string]]::new()
    foreach ($channel in @('queue', 'issues', 'proposals', 'alerts', 'brief')) {
        if ([string]::IsNullOrWhiteSpace($config.discord.webhooks[$channel])) { $todo.Add("config.json discord.webhooks.${channel}: webhook URL of #$channel") }
    }
    if ([string]::IsNullOrWhiteSpace($config.discord.issuesChannelId)) { $todo.Add('config.json discord.issuesChannelId: id of the #issues channel') }
    if (-not (Get-DiscordBotToken)) { $todo.Add('WINO_WORK_DISCORD_BOT_TOKEN (or DISCORD_BOT_TOKEN in ~/.claude/channels/discord/.env): bot token for issue threads') }
    foreach ($variable in @('WINO_R2_ACCOUNT_ID', 'WINO_R2_ACCESS_KEY_ID', 'WINO_R2_SECRET_ACCESS_KEY')) {
        if (-not [Environment]::GetEnvironmentVariable($variable)) { $todo.Add("$variable environment variable") }
    }
    if ($todo.Count) { Write-Output 'Still to fill in:'; $todo | ForEach-Object { Write-Output "  - $_" } } else { Write-Output 'Configuration is complete.' }
}

function Invoke-Mac {
    $sub = if ($Arguments.Count) { $Arguments[0] } else { '' }
    switch ($sub) {
        'ping' {
            $reachable = Test-Mac
            Write-Output "$((Get-Config).macHost) $(if ($reachable) { 'reachable' } else { 'unreachable' })"
            if (-not $reachable) { $script:ExitCode = 1 }
        }
        { $_ -in 'sync', 'build', 'test' } { Write-Output "mac $sub is not implemented (phase 4)."; $script:ExitCode = 3 }
        default { throw "Unknown mac command '$sub'. Use mac ping." }
    }
}

$script:ExitCode = 0
try {
    switch ($Command.ToLowerInvariant()) {
        { $_ -in 'help', '-h', '--help', '/?' } { Write-Output $script:HelpText }
        'init' { Invoke-Init }
        'status' { Invoke-Status }
        'start' { Invoke-Start }
        'stop' { Invoke-Stop }
        'ask' { Invoke-Ask }
        'answer' { Invoke-Answer }
        'publish' {
            $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
            $url = Publish-Artifact $number (Get-Argument 1 'file path') $As $Name
            Write-Output $url
            Write-Output "Issue page: $(Get-PublicUrl "$number/index.html")"
        }
        'notify' {
            $number = ConvertTo-IssueNumber (Get-Argument 0 'issue number')
            Write-Output "Notification $(Send-Notification 'issues' (Get-Argument 1 'message text') $number)."
        }
        'alert' { Write-Output "Alert $(Send-Alert (Get-Argument 0 'alert text') -Critical:$Critical)." }
        'brief' { Invoke-Brief }
        'tick' { Invoke-Tick }
        'boot' { Invoke-Boot }
        'pause' { $null = Update-State { param($s) $s.paused = $true }; Write-Output 'paused: true (idle prototyping stopped; start still works)' }
        'resume' { $null = Update-State { param($s) $s.paused = $false }; Write-Output 'paused: false' }
        'mac' { Invoke-Mac }
        { $_ -in 'hardstops', 'merge' } { Write-Output "$_ is not implemented (phase 3)."; $script:ExitCode = 3 }
        'parity' { Write-Output 'parity is not implemented (phase 4).'; $script:ExitCode = 3 }
        'prototype' { Write-Output 'prototype is not implemented (phase 5).'; $script:ExitCode = 3 }
        'release' { Write-Output 'release is not implemented (phase 7).'; $script:ExitCode = 3 }
        default { throw "Unknown command '$Command'. Run work.ps1 help." }
    }
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    $script:ExitCode = 1
}
exit $script:ExitCode
