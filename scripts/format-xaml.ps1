[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Changed,
    [string[]]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$configurationPath = Join-Path $repositoryRoot "Settings.XamlStyler"
$toolManifestPath = Join-Path $repositoryRoot ".config\dotnet-tools.json"
$defaultRoots = @(
    "src\Wino.Mail.WinUI",
    "controls\Wino.Mail.Controls",
    "controls\Wino.Mail.Controls.Playground",
    "controls\Wino.Editor"
)

function Get-XamlFilesFromPath {
    param([Parameter(Mandatory)][string[]]$InputPath)

    $files = [System.Collections.Generic.List[string]]::new()

    foreach ($candidate in $InputPath) {
        $fullPath = if ([System.IO.Path]::IsPathRooted($candidate)) {
            $candidate
        }
        else {
            Join-Path $repositoryRoot $candidate
        }

        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "XAML path does not exist: $candidate"
        }

        $item = Get-Item -LiteralPath $fullPath

        if ($item.PSIsContainer) {
            Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Filter "*.xaml" |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                ForEach-Object { $files.Add($_.FullName) }
        }
        elseif ($item.Extension -ieq ".xaml") {
            $files.Add($item.FullName)
        }
        else {
            throw "The path is not a XAML file or directory: $candidate"
        }
    }

    return $files
}

function Get-ChangedXamlFiles {
    $relativeFiles = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    $trackedFiles = & git -C $repositoryRoot diff HEAD --name-only --diff-filter=ACMR -- "*.xaml"
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not list changed XAML files."
    }

    $untrackedFiles = & git -C $repositoryRoot ls-files --others --exclude-standard -- "*.xaml"
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not list untracked XAML files."
    }

    foreach ($relativePath in @($trackedFiles) + @($untrackedFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($relativePath)) {
            [void]$relativeFiles.Add($relativePath)
        }
    }

    $existingFiles = foreach ($relativePath in $relativeFiles) {
        $fullPath = Join-Path $repositoryRoot $relativePath

        if (Test-Path -LiteralPath $fullPath) {
            (Resolve-Path -LiteralPath $fullPath).Path
        }
    }

    return $existingFiles
}

function Invoke-XamlStylerChunk {
    param([Parameter(Mandatory)][string[]]$Files)

    $arguments = @(
        "tool", "run", "xstyler", "--",
        "--file", ($Files -join ","),
        "--config", $configurationPath,
        "--loglevel", "Minimal"
    )

    if ($Check) {
        $arguments += "--passive"
    }

    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        $operation = if ($Check) { "verification" } else { "formatting" }
        throw "XAML Styler $operation failed for $($Files.Count) file(s)."
    }
}

Set-Location $repositoryRoot

if (-not (Test-Path -LiteralPath $configurationPath)) {
    throw "XAML Styler configuration was not found: $configurationPath"
}

if (-not (Test-Path -LiteralPath $toolManifestPath)) {
    throw "The repository tool manifest was not found: $toolManifestPath"
}

$toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw | ConvertFrom-Json
$toolVersion = $toolManifest.tools.'xamlstyler.console'.version
$nugetPackagesPath = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $env:USERPROFILE ".nuget\packages"
}
else {
    $env:NUGET_PACKAGES
}
$toolPackagePath = Join-Path $nugetPackagesPath "xamlstyler.console\$toolVersion"

if (-not (Test-Path -LiteralPath $toolPackagePath)) {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "The repository .NET tools could not be restored."
    }
}

$xamlFiles = if ($Changed) {
    @(Get-ChangedXamlFiles)
}
elseif ($Path -and $Path.Count -gt 0) {
    @(Get-XamlFilesFromPath -InputPath $Path)
}
else {
    @(Get-XamlFilesFromPath -InputPath $defaultRoots)
}

$xamlFiles = @($xamlFiles | Sort-Object -Unique)

if ($xamlFiles.Count -eq 0) {
    Write-Host "No XAML files matched the request."
    exit 0
}

$operationName = if ($Check) { "Verifying" } else { "Formatting" }
Write-Host "$operationName $($xamlFiles.Count) XAML file(s) with Settings.XamlStyler."

$chunk = [System.Collections.Generic.List[string]]::new()
$chunkLength = 0

foreach ($xamlFile in $xamlFiles) {
    $nextLength = $chunkLength + $xamlFile.Length + 1

    if ($chunk.Count -gt 0 -and $nextLength -gt 12000) {
        Invoke-XamlStylerChunk -Files $chunk.ToArray()
        $chunk.Clear()
        $chunkLength = 0
    }

    $chunk.Add($xamlFile)
    $chunkLength += $xamlFile.Length + 1
}

if ($chunk.Count -gt 0) {
    Invoke-XamlStylerChunk -Files $chunk.ToArray()
}

$result = if ($Check) { "XAML formatting is valid." } else { "XAML formatting is complete." }
Write-Host $result -ForegroundColor Green
