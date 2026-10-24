#requires -Version 7.0
<#
.SYNOPSIS
Prepares and publishes stable Windows releases: version, release notes, GitHub release, and issue updates.
.DESCRIPTION
Commands:
  bump <x.y.z>            Set Package.appxmanifest to x.y.z.0. Commit the change yourself.
  mark <N...>             Add the 'pending release' label to issues.
  unmark <N...>           Remove the 'pending release' label from issues.
  add "<text>"            Add a line to release-notes/unreleased.md for work outside this repository.
  notes [<x.y.z>]         Draft release-notes/<x.y.z>.md with Claude Code from the commits since the previous
                          v* tag (or -Since), the 'pending release' issues, Refs: trailers and unreleased.md.
  publish <x.y.z>         Tag -Ref as vx.y.z.0, create the GitHub release (marked Latest), then remove the label
                          from, comment on and close every issue linked in the notes. -DryRun only renders the
                          texts into release-notes/.out/<x.y.z>; run it before the Store submission for store.txt.
                          -Draft pushes the tag and creates a draft release while the Store build is in review,
                          leaving the issues alone. A later publish turns the draft into the Latest release.

Run it from any directory, also from sibling repositories. Requires git, gh (signed in) and, for notes, claude.
See docs/releases.md and release-notes/README.md.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('bump', 'mark', 'unmark', 'add', 'notes', 'publish')]
    [string]$Command,
    [Parameter(Position = 1, ValueFromRemainingArguments)]
    [string[]]$Arguments = @(),
    [string]$Since,
    [string]$Ref = 'HEAD',
    [string]$Model,
    [switch]$Force,
    [switch]$DryRun,
    [switch]$Draft,
    [switch]$Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script:Repository = 'bkaankose/Wino-Mail'
$script:IssueUrl = "https://github.com/$($script:Repository)/issues/"
$script:Label = 'pending release'
$script:NotesFolder = Join-Path $script:Root 'release-notes'
$script:ManifestPath = Join-Path $script:Root 'src/Wino.Mail.WinUI/Package.appxmanifest'
$script:WhatsNewFolder = Join-Path $script:Root 'src/Wino.Mail.WinUI/Assets/WhatsNew'
$script:StoreUri = 'https://apps.microsoft.com/detail/9NCRCVJC50WL?referrer=appbadge&mode=direct'
$script:StoreTextLimit = 1500
$script:SocialTextLimit = 280
# Changes under these paths never reach the Windows app, so they never reach its release notes.
$script:NonWindowsPathPattern = '^(src/Wino\.(Mail\.(MacOS|iOS|UIKit|Android)|Platform\.(MacOS|iOS|Android|Apple)|Core\.(Apple|MacOS)\.Bindings)|controls/Wino\.[A-Za-z.]+\.(Android|AppKit|UIKit|WebKit)/|build/Wino\.(MacOS|iOS|Android)|scripts/[^/]+/(macos|ios|android)/|WinoMail\.(MacOS|iOS|Android)\.slnx$|docs/|\.claude/|\.vscode/|AGENTS\.md$|CLAUDE\.md$)'

function Invoke-Native {
    param([string]$FilePath, [string[]]$ArgumentList, [switch]$AllowFailure)

    $output = & $FilePath @ArgumentList 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($ArgumentList -join ' ') failed with exit code $LASTEXITCODE.`n$($output -join "`n")"
    }
    return @($output | ForEach-Object { "$_" })
}

function Invoke-Git { param([string[]]$GitArgs, [switch]$AllowFailure) Invoke-Native git (@('-C', $script:Root) + $GitArgs) -AllowFailure:$AllowFailure }
function Invoke-Gh { param([string[]]$GhArgs, [switch]$AllowFailure) Invoke-Native gh $GhArgs -AllowFailure:$AllowFailure }
function Get-GhJson { param([string[]]$GhArgs) ((Invoke-Gh $GhArgs) -join "`n") | ConvertFrom-Json }

function Assert-ReleaseVersion {
    param([string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Use a three-part version such as 2.2.6, not '$Version'." }
}

function Get-ManifestVersion {
    $manifest = [xml](Get-Content -LiteralPath $script:ManifestPath -Raw)
    return ([version][string]$manifest.Package.Identity.Version).ToString(3)
}

function Get-TagName { param([string]$Version) "v$Version.0" }

function Read-YesNo {
    param([string]$Prompt)
    if ($Yes) { return $true }
    while ($true) {
        $answer = Read-Host "$Prompt (yes/no)"
        if ($null -eq $answer) { return $false }
        switch ($answer.Trim().ToLowerInvariant()) { { $_ -in 'yes', 'y' } { return $true } { $_ -in 'no', 'n' } { return $false } }
    }
}

function Get-IssueNumbers {
    param([string[]]$Values)
    $numbers = foreach ($value in $Values) {
        foreach ($part in ($value -split '[\s,]+')) {
            if ($part -match '^#?(\d+)$') { [int]$Matches[1] } elseif ($part) { throw "'$part' is not an issue number." }
        }
    }
    if (@($numbers).Count -eq 0) { throw 'Give at least one issue number.' }
    return @($numbers | Sort-Object -Unique)
}

function Read-ReleaseNotes {
    param([string]$Path)

    $text = (Get-Content -LiteralPath $Path -Raw) -replace "`r`n", "`n"
    $notes = [ordered]@{ Version = ''; Base = ''; Intro = ''; Sections = [ordered]@{} }
    if ($text -match '(?s)^---\n(.*?)\n---\n(.*)$') {
        $frontMatter = $Matches[1]
        $text = $Matches[2]
        foreach ($line in ($frontMatter -split "`n")) {
            if ($line -match '^version:\s*(.+)$') { $notes.Version = $Matches[1].Trim() }
            if ($line -match '^base:\s*(.+)$') { $notes.Base = $Matches[1].Trim() }
        }
    }
    $parts = [regex]::Split($text, '(?m)^## +(.+?)\s*$')
    $notes.Intro = $parts[0].Trim()
    for ($i = 1; $i -lt $parts.Count; $i += 2) { $notes.Sections[$parts[$i].Trim()] = $parts[$i + 1].Trim() }
    return [pscustomobject]$notes
}

function Get-NotesSection {
    param([object]$Notes, [string]$Name)
    if (-not $Notes.Sections.Contains($Name)) { return '' }
    $content = [string]$Notes.Sections[$Name]
    if ($content -match '^_?None_?\.?$') { return '' }
    return $content
}

function Get-PublishedIssueNumbers {
    param([object]$Notes)
    $text = @('Highlights', 'New and improved', 'Fixes' | ForEach-Object { Get-NotesSection $Notes $_ }) -join "`n"
    $pattern = [regex]::Escape($script:IssueUrl) + '(\d+)'
    return @([regex]::Matches($text, $pattern) | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
}

function Invoke-Bump {
    if ($Arguments.Count -ne 1) { throw 'Usage: release.ps1 bump <x.y.z>' }
    $version = $Arguments[0]
    Assert-ReleaseVersion $version
    # Keep the manifest's byte order mark and line endings; only the version changes.
    $bytes = [IO.File]::ReadAllBytes($script:ManifestPath)
    $encoding = [Text.UTF8Encoding]::new($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $content = [IO.File]::ReadAllText($script:ManifestPath)
    $pattern = '(?s)(<Identity\b[^>]*?\bVersion=")[^"]+(")'
    if ($content -notmatch $pattern) { throw "No Identity Version in $($script:ManifestPath)." }
    $updated = [regex]::Replace($content, $pattern, "`${1}$version.0`${2}", 1)
    [IO.File]::WriteAllText($script:ManifestPath, $updated, $encoding)
    Write-Host "Package.appxmanifest is now $version.0. Commit it, then run: release.ps1 notes" -ForegroundColor Green
}

function Invoke-Mark {
    param([bool]$Add)
    $numbers = Get-IssueNumbers $Arguments
    $existing = Get-GhJson @('label', 'list', '--repo', $script:Repository, '--search', $script:Label, '--json', 'name')
    if ($Add -and -not @($existing | Where-Object name -EQ $script:Label)) {
        Invoke-Gh @('label', 'create', $script:Label, '--repo', $script:Repository, '--color', '0E8A16',
            '--description', 'Addressed on main; ships in the next stable release.') | Out-Null
    }
    $flag = if ($Add) { '--add-label' } else { '--remove-label' }
    foreach ($number in $numbers) {
        Invoke-Gh @('issue', 'edit', "$number", '--repo', $script:Repository, $flag, $script:Label) | Out-Null
        Write-Host "#$number $(if ($Add) { 'marked' } else { 'unmarked' })"
    }
}

function Invoke-Add {
    $text = ($Arguments -join ' ').Trim()
    if (-not $text) { throw 'Usage: release.ps1 add "<user-facing change>"' }
    $path = Join-Path $script:NotesFolder 'unreleased.md'
    Add-Content -LiteralPath $path -Value "- $($text -replace '^[-*]\s*', '')"
    Write-Host "Added to release-notes/unreleased.md." -ForegroundColor Green
}

function Get-ReleaseCommits {
    param([string]$Base)

    $hashes = Invoke-Git @('log', '--no-merges', '--reverse', '--format=%H', "$Base..HEAD")
    foreach ($hash in $hashes) {
        $message = @(Invoke-Git @('show', '-s', '--format=%s%n%b', $hash)) -join "`n"
        $files = @(Invoke-Git @('diff-tree', '--no-commit-id', '--name-only', '-r', '--root', $hash) | Where-Object { $_ })
        $windowsFiles = @($files | Where-Object { $_ -notmatch $script:NonWindowsPathPattern })
        [pscustomobject]@{
            Hash = $hash.Substring(0, 8)
            Subject = ($message -split "`n")[0]
            Body = (($message -split "`n" | Select-Object -Skip 1) -join "`n").Trim()
            Files = $files
            WindowsFiles = $windowsFiles
            Refs = @([regex]::Matches($message, '(?im)^Refs:\s*(.+)$') | ForEach-Object { [regex]::Matches($_.Groups[1].Value, '#(\d+)') } | ForEach-Object { [int]$_.Groups[1].Value })
            Highlight = $message -match '(?im)^Highlight:\s*yes\s*$'
        }
    }
}

function Format-IssueLine {
    param([object]$Issue)
    $labels = @($Issue.labels | ForEach-Object name | Where-Object { $_ -ne $script:Label })
    $suffix = if ($labels.Count) { " [labels: $($labels -join ', ')]" } else { '' }
    return "- #$($Issue.number) ($($Issue.state)) $($Issue.title)$suffix"
}

function Invoke-Notes {
    $version = if ($Arguments.Count) { $Arguments[0] } else { Get-ManifestVersion }
    Assert-ReleaseVersion $version
    $target = Join-Path $script:NotesFolder "$version.md"
    if ((Test-Path -LiteralPath $target) -and -not $Force) { throw "release-notes/$version.md exists. Edit it, or pass -Force to draft it again." }

    $base = if ($Since) { $Since } else { @(Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*', 'HEAD'))[0] }
    $baseDate = @(Invoke-Git @('log', '-1', '--format=%cs', $base))[0]
    Write-Host "Collecting changes from $base ($baseDate) to HEAD..."

    $commits = @(Get-ReleaseCommits $base)
    $windowsCommits = @($commits | Where-Object { $_.WindowsFiles.Count -gt 0 -or $_.Files.Count -eq 0 })
    $skipped = $commits.Count - $windowsCommits.Count

    $issueFields = 'number,title,state,labels'
    $labeled = @(Get-GhJson @('issue', 'list', '--repo', $script:Repository, '--label', $script:Label, '--state', 'open', '--limit', '300', '--json', $issueFields))
    $labeled += @(Get-GhJson @('issue', 'list', '--repo', $script:Repository, '--label', $script:Label, '--state', 'closed', '--search', "closed:>=$baseDate", '--limit', '300', '--json', $issueFields))
    $known = @{}
    foreach ($issue in $labeled) { $known[[int]$issue.number] = $issue }

    $refIssues = foreach ($number in @($windowsCommits | ForEach-Object Refs | Sort-Object -Unique)) {
        if ($known.ContainsKey($number)) { continue }
        $issue = Get-GhJson @('issue', 'view', "$number", '--repo', $script:Repository, '--json', $issueFields)
        $known[$number] = $issue
        $issue
    }
    $closed = @(Get-GhJson @('issue', 'list', '--repo', $script:Repository, '--state', 'closed', '--search', "closed:>=$baseDate reason:completed", '--limit', '300', '--json', $issueFields) |
        Where-Object { -not $known.ContainsKey([int]$_.number) })

    $unreleasedPath = Join-Path $script:NotesFolder 'unreleased.md'
    $unreleased = @(if (Test-Path -LiteralPath $unreleasedPath) { Get-Content -LiteralPath $unreleasedPath | Where-Object { $_.Trim() } })

    $context = [Text.StringBuilder]::new()
    [void]$context.AppendLine("### Commits ($($windowsCommits.Count))").AppendLine()
    foreach ($commit in $windowsCommits) {
        $flags = @()
        if ($commit.Refs.Count) { $flags += 'Refs: ' + (($commit.Refs | ForEach-Object { "#$_" }) -join ', ') }
        if ($commit.Highlight) { $flags += 'Highlight: yes' }
        $flagText = if ($flags.Count) { " [$($flags -join '; ')]" } else { '' }
        [void]$context.AppendLine("- $($commit.Hash) $($commit.Subject)$flagText")
        if ($commit.Body) {
            $body = $commit.Body -replace '(?im)^(Refs|Highlight):.*$', '' -replace '\n{2,}', "`n"
            if ($body.Length -gt 800) { $body = $body.Substring(0, 800) + '...' }
            if ($body.Trim()) { [void]$context.AppendLine(($body.Trim() -split "`n" | ForEach-Object { "    $_" }) -join "`n") }
        }
        $shown = @($commit.WindowsFiles | Select-Object -First 12)
        $more = if ($commit.WindowsFiles.Count -gt $shown.Count) { " (+$($commit.WindowsFiles.Count - $shown.Count) more)" } else { '' }
        [void]$context.AppendLine("    files: $($shown -join ', ')$more")
    }
    [void]$context.AppendLine().AppendLine("### Issues marked '$($script:Label)'").AppendLine()
    if ($labeled.Count) { $labeled | ForEach-Object { [void]$context.AppendLine((Format-IssueLine $_)) } } else { [void]$context.AppendLine('_None_') }
    [void]$context.AppendLine().AppendLine('### Issues named in Refs: trailers').AppendLine()
    if (@($refIssues).Count) { $refIssues | ForEach-Object { [void]$context.AppendLine((Format-IssueLine $_)) } } else { [void]$context.AppendLine('_None_') }
    [void]$context.AppendLine().AppendLine('### Lines from unreleased.md').AppendLine()
    if ($unreleased.Count) { $unreleased | ForEach-Object { [void]$context.AppendLine($_) } } else { [void]$context.AppendLine('_None_') }
    [void]$context.AppendLine().AppendLine("### Other issues closed since $baseDate").AppendLine()
    if ($closed.Count) { $closed | ForEach-Object { [void]$context.AppendLine((Format-IssueLine $_)) } } else { [void]$context.AppendLine('_None_') }

    $prompt = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-notes-prompt.md') -Raw).
        Replace('{{VERSION}}', $version).
        Replace('{{BASE}}', $base).
        Replace('{{FORMAT}}', (Get-Content -LiteralPath (Join-Path $script:NotesFolder 'README.md') -Raw)).
        Replace('{{INPUT}}', $context.ToString())

    Write-Host "$($windowsCommits.Count) commits ($skipped skipped as non-Windows or internal), $($labeled.Count) labeled issues, $(@($refIssues).Count) more from Refs:, $($unreleased.Count) unreleased.md lines, $($closed.Count) other closed issues."
    Write-Host 'Drafting with Claude Code. This takes a few minutes...'

    $claudeArgs = @('-p', '--output-format', 'text', '--allowedTools', 'Bash(git show:*),Bash(git log:*),Bash(git diff:*),Bash(gh issue view:*)')
    if ($Model) { $claudeArgs += @('--model', $Model) }
    Push-Location $script:Root
    try { $draft = ($prompt | & claude @claudeArgs) -join "`n" }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "claude exited with code $LASTEXITCODE.`n$draft" }

    $draft = $draft.Trim() -replace '^```(markdown)?\n', '' -replace '\n```$', ''
    $start = $draft.IndexOf("---`n")
    if ($start -lt 0) { throw "The draft has no front matter. Claude returned:`n$draft" }
    $draft = $draft.Substring($start).Trim() + "`n"

    New-Item -ItemType Directory -Force -Path $script:NotesFolder | Out-Null
    [IO.File]::WriteAllText($target, $draft)
    if ($unreleased.Count) { Remove-Item -LiteralPath $unreleasedPath }
    Write-Host "Wrote release-notes/$version.md. Review it: move items in or out of Highlights and resolve the Review section." -ForegroundColor Green
}

function ConvertTo-PlainText {
    param([string]$Markdown)
    $text = $Markdown -replace '\s*\(\[#\d+\]\([^)]*\)(,\s*\[#\d+\]\([^)]*\))*\)', ''
    $text = $text -replace '\[([^\]]+)\]\([^)]*\)', '$1' -replace '\*\*([^*]+)\*\*', '$1' -replace '`([^`]+)`', '$1'
    return $text -replace '(?m)^\s*[-*]\s+', '• '
}

function Get-ReleaseBody {
    param([object]$Notes, [string]$Version, [string]$Tag)

    $body = [Text.StringBuilder]::new()
    [void]$body.AppendLine("# Wino Mail $Version").AppendLine()
    if ($Notes.Intro) { [void]$body.AppendLine($Notes.Intro).AppendLine() }
    [void]$body.AppendLine("<a href=""$($script:StoreUri)"">").
        AppendLine('  <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Download Wino Mail from Microsoft Store">').
        AppendLine('</a>').AppendLine().
        AppendLine("**Can't use Microsoft Store?** Install the stable sideload build with App Installer: [WinoMail.appinstaller](https://download.winomail.app/WinoMail.appinstaller)").AppendLine().
        AppendLine('> [!NOTE]').
        AppendLine('> The sideload build gets its updates from the Wino download feed, not from Microsoft Store. Install it from the `.appinstaller` link above. It has its own package identity, so it can be installed next to the Store version if you want to.').AppendLine()

    foreach ($name in 'Highlights', 'New and improved', 'Fixes') {
        $content = Get-NotesSection $Notes $name
        if (-not $content) { continue }
        [void]$body.AppendLine("## $name").AppendLine().AppendLine($content).AppendLine()
        if ($name -ne 'Highlights') { continue }
        $whatsNew = Join-Path $script:WhatsNewFolder "$Version.json"
        if (-not (Test-Path -LiteralPath $whatsNew)) { continue }
        foreach ($feature in @((Get-Content -LiteralPath $whatsNew -Raw | ConvertFrom-Json).features)) {
            $src = "https://raw.githubusercontent.com/$($script:Repository)/$Tag/src/Wino.Mail.WinUI/Assets/WhatsNew/$($feature.image)"
            [void]$body.AppendLine("<img src=""$src"" width=""560"" alt=""$([Net.WebUtility]::HtmlEncode([string]$feature.title))"">")
        }
        [void]$body.AppendLine()
    }
    return $body.ToString().TrimEnd() + "`n"
}

function Get-StoreText {
    param([object]$Notes, [string]$ReleaseUrl)

    $footer = "Full release notes: $ReleaseUrl"
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($name in 'Highlights', 'New and improved', 'Fixes') {
        $content = Get-NotesSection $Notes $name
        if (-not $content) { continue }
        $items = @((ConvertTo-PlainText $content) -split "`n" | Where-Object { $_.Trim() })
        $candidate = @($lines) + @('', "$name`:") + $items
        $length = (($candidate + @('', $footer)) -join "`n").Trim().Length
        if ($length -le $script:StoreTextLimit) { $lines.AddRange([string[]](@('', "$name`:") + $items)); continue }
        # Add whole items until the limit, then point to the full notes.
        $lines.AddRange([string[]]@('', "$name`:"))
        foreach ($item in $items) {
            if (((@($lines) + @($item, '…', '', $footer)) -join "`n").Trim().Length -gt $script:StoreTextLimit) { $lines.Add('…'); break }
            $lines.Add($item)
        }
        break
    }
    return ((@($lines) + @('', $footer)) -join "`n").Trim() + "`n"
}

function Get-SocialText {
    param([object]$Notes, [string]$Version, [string]$ReleaseUrl)

    $titles = @([regex]::Matches((Get-NotesSection $Notes 'Highlights'), '(?m)^\s*[-*]\s+\*\*(.+?)\.?\*\*') | ForEach-Object { $_.Groups[1].Value })
    $changes = @('New and improved', 'Fixes' | ForEach-Object { (Get-NotesSection $Notes $_) -split "`n" } | Where-Object { $_ -match '^\s*[-*]\s+' }).Count
    $more = if ($changes -eq 1) { 'one more improvement' } else { "$changes more fixes and improvements" }
    $summary = if (-not $titles.Count) { $more -replace '^one more', 'one' -replace ' more ', ' ' }
        elseif ($changes) { ($titles -join ', ') + ", and $more" }
        else { $titles -join ', ' }
    $text = "Wino Mail $Version is out in the Microsoft Store: $summary. $ReleaseUrl"
    if ($text.Length -gt $script:SocialTextLimit) { $text = "Wino Mail $Version is out in the Microsoft Store. What's new: $ReleaseUrl" }
    return $text + "`n"
}

function Invoke-Publish {
    if ($Arguments.Count -ne 1) { throw 'Usage: release.ps1 publish <x.y.z> [-Ref <commit>] [-DryRun | -Draft] [-Yes]' }
    $version = $Arguments[0]
    Assert-ReleaseVersion $version
    $notesPath = Join-Path $script:NotesFolder "$version.md"
    if (-not (Test-Path -LiteralPath $notesPath)) { throw "release-notes/$version.md is missing. Run release.ps1 notes first." }
    $notes = Read-ReleaseNotes $notesPath
    $tag = Get-TagName $version
    $releaseUrl = "https://github.com/$($script:Repository)/releases/tag/$tag"

    $tagExists = @(Invoke-Git @('tag', '--list', $tag)).Count -gt 0
    $commit = if ($tagExists) { @(Invoke-Git @('rev-list', '-n', '1', $tag))[0] } else { @(Invoke-Git @('rev-parse', $Ref))[0] }
    $commitLine = @(Invoke-Git @('log', '-1', '--format=%h %cs %s', $commit))[0]

    $outFolder = Join-Path $script:NotesFolder ".out/$version"
    New-Item -ItemType Directory -Force -Path $outFolder | Out-Null
    $bodyPath = Join-Path $outFolder 'github.md'
    [IO.File]::WriteAllText($bodyPath, (Get-ReleaseBody $notes $version $tag))
    [IO.File]::WriteAllText((Join-Path $outFolder 'store.txt'), (Get-StoreText $notes $releaseUrl))
    [IO.File]::WriteAllText((Join-Path $outFolder 'social.txt'), (Get-SocialText $notes $version $releaseUrl))

    $issues = @(foreach ($number in (Get-PublishedIssueNumbers $notes)) {
        Get-GhJson @('issue', 'view', "$number", '--repo', $script:Repository, '--json', 'number,title,state,labels')
    })
    $linked = @{}
    foreach ($issue in $issues) { $linked[[int]$issue.number] = $true }
    $forgotten = @(Get-GhJson @('issue', 'list', '--repo', $script:Repository, '--label', $script:Label, '--state', 'open', '--limit', '300', '--json', 'number,title') |
        Where-Object { -not $linked.ContainsKey([int]$_.number) })

    Write-Host "Release:  $tag $(if ($tagExists) { '(existing tag)' } else { '(new tag)' }) at $commitLine"
    Write-Host "Texts:    $outFolder (github.md, store.txt, social.txt)"
    Write-Host "Issues to comment on and close ($($issues.Count)):"
    $issues | ForEach-Object { Write-Host "  #$($_.number) [$($_.state)] $($_.title)" }
    if ($forgotten.Count) {
        Write-Warning "These '$($script:Label)' issues are not linked in the notes and stay as they are:"
        $forgotten | ForEach-Object { Write-Warning "  #$($_.number) $($_.title)" }
    }
    if (Get-NotesSection $notes 'Review') { Write-Warning 'The Review section still has items. It is never published; resolve it before publishing.' }
    $whatsNew = Join-Path $script:WhatsNewFolder "$version.json"
    if ((Get-NotesSection $notes 'Highlights') -and -not (Test-Path -LiteralPath $whatsNew)) {
        Write-Warning "The notes have Highlights but Assets/WhatsNew/$version.json is missing. Run the whatsnew skill."
    }
    if ($DryRun) {
        Write-Host "Dry run: nothing was tagged, pushed, published, or changed on GitHub. Paste store.txt into Partner Center's 'What's new in this version' when you submit." -ForegroundColor Yellow
        return
    }

    $existing = $null
    if ($tagExists) {
        $view = (@(Invoke-Gh @('release', 'view', $tag, '--repo', $script:Repository, '--json', 'isDraft') -AllowFailure) -join "`n")
        if ($view -match 'isDraft') { $existing = $view | ConvertFrom-Json }
    }
    if ($existing -and (-not $existing.isDraft -or $Draft)) {
        throw "A GitHub $(if ($existing.isDraft) { 'draft ' })release for $tag already exists."
    }
    $question = if ($Draft) { "Push $tag and create a draft release? Issues stay unchanged." }
        elseif ($existing) { "Publish the draft $tag as Latest, then comment on and close $($issues.Count) issues?" }
        else { "Publish $tag, then comment on and close $($issues.Count) issues?" }
    if (-not (Read-YesNo $question)) { Write-Host 'Stopped.'; return }

    if (-not $tagExists) { Invoke-Git @('tag', '-a', $tag, $commit, '-m', "Wino Mail $version") | Out-Null }
    $tagsToPush = @($tag)
    if ($notes.Base -match '^v[0-9.]+$' -and -not @(Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$($notes.Base)"))) { $tagsToPush += $notes.Base }
    Invoke-Git (@('push', 'origin') + ($tagsToPush | ForEach-Object { "refs/tags/$_" })) | Out-Null
    if ($Draft) {
        $draftUrl = @(Invoke-Gh @('release', 'create', $tag, '--repo', $script:Repository, '--verify-tag', '--draft', '--title', "Wino Mail $version", '--notes-file', $bodyPath))[-1]
        Write-Host "Created draft $draftUrl. Run publish without -Draft once the Store build is live." -ForegroundColor Green
        return
    }
    if ($existing) {
        Invoke-Gh @('release', 'edit', $tag, '--repo', $script:Repository, '--draft=false', '--latest', '--title', "Wino Mail $version", '--notes-file', $bodyPath) | Out-Null
    }
    else {
        Invoke-Gh @('release', 'create', $tag, '--repo', $script:Repository, '--verify-tag', '--latest', '--title', "Wino Mail $version", '--notes-file', $bodyPath) | Out-Null
    }
    Write-Host "Published $releaseUrl" -ForegroundColor Green

    $comment = "This is addressed in Wino Mail $version, which is now available in the Microsoft Store. Thanks for reporting it!`n`nRelease notes: $releaseUrl"
    foreach ($issue in $issues) {
        $number = "$($issue.number)"
        if (@($issue.labels | Where-Object name -EQ $script:Label)) { Invoke-Gh @('issue', 'edit', $number, '--repo', $script:Repository, '--remove-label', $script:Label) | Out-Null }
        Invoke-Gh @('issue', 'comment', $number, '--repo', $script:Repository, '--body', $comment) | Out-Null
        if ($issue.state -eq 'OPEN') { Invoke-Gh @('issue', 'close', $number, '--repo', $script:Repository, '--reason', 'completed') | Out-Null }
        Write-Host "  #$number done"
    }
    Write-Host "Share social.txt from $outFolder." -ForegroundColor Green
}

switch ($Command) {
    'bump' { Invoke-Bump }
    'mark' { Invoke-Mark $true }
    'unmark' { Invoke-Mark $false }
    'add' { Invoke-Add }
    'notes' { Invoke-Notes }
    'publish' { Invoke-Publish }
}
