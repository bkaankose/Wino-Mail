#requires -Version 7.0
<#
.SYNOPSIS
Validates the What's New release notes bundled with the app.
.DESCRIPTION
Checks src/Wino.Mail.WinUI/Assets/WhatsNew/<version>.json for the given version (default: the
Package.appxmanifest version) and every other notes file in the folder. Each feature needs a
title, a description, and a 1120x600 PNG illustration with a descriptive kebab-case name.
Exits with code 1 when a check fails. Dot-source the script to use Get-WhatsNewValidationErrors.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:WhatsNewImageWidth = 1120
$script:WhatsNewImageHeight = 600
$script:WhatsNewImageNamePattern = '^[a-z0-9]+(-[a-z0-9]+)+\.png$'

function Get-WhatsNewManifestVersion {
    param([string]$Root)

    $manifestPath = Join-Path $Root 'src/Wino.Mail.WinUI/Package.appxmanifest'
    $manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    $packageVersion = [version][string]$manifest.Package.Identity.Version
    return '{0}.{1}.{2}' -f $packageVersion.Major, $packageVersion.Minor, $packageVersion.Build
}

function Get-PngSize {
    param([string]$Path)

    # Width and height are the first two big-endian integers of the IHDR chunk.
    $bytes = [byte[]]::new(24)
    $stream = [IO.File]::OpenRead($Path)
    try { $read = $stream.Read($bytes, 0, 24) } finally { $stream.Dispose() }
    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($read -lt 24 -or @(Compare-Object $bytes[0..7] $signature -SyncWindow 0).Count -gt 0) { return $null }
    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Get-WhatsNewValidationErrors {
    param([string]$Root, [string]$RequiredVersion)

    $Root = [IO.Path]::GetFullPath($Root)
    $folder = Join-Path $Root 'src/Wino.Mail.WinUI/Assets/WhatsNew'
    $errors = [Collections.Generic.List[string]]::new()
    if (-not $RequiredVersion) { $RequiredVersion = Get-WhatsNewManifestVersion $Root }

    $project = Get-Content -LiteralPath (Join-Path $Root 'src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj') -Raw
    if ($project -notmatch '<Content Include="Assets\\WhatsNew\\\*\.json"') {
        $errors.Add('Wino.Mail.WinUI.csproj must contain <Content Include="Assets\WhatsNew\*.json" />.')
    }
    if ($project -match '<Content Remove="Assets\\WhatsNew') {
        $errors.Add('Wino.Mail.WinUI.csproj removes What''s New assets from the package.')
    }

    $files = if (Test-Path -LiteralPath $folder) { @(Get-ChildItem -LiteralPath $folder -Filter '*.json' -File) } else { @() }
    $versions = @{}
    foreach ($file in $files) {
        $name = $file.Name
        try { $release = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json }
        catch { $errors.Add("${name}: invalid JSON. $($_.Exception.Message)"); continue }

        $releaseVersion = [string]$release.version
        if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { $errors.Add("${name}: 'version' must have three parts, such as 2.1.3."); continue }
        if ($file.BaseName -ne $releaseVersion) { $errors.Add("${name}: the file name must be $releaseVersion.json.") }
        if ($versions.ContainsKey($releaseVersion)) { $errors.Add("${name}: version $releaseVersion is also in $($versions[$releaseVersion]).") }
        $versions[$releaseVersion] = $name
        if ($null -ne $release.PSObject.Properties['isStarred'] -and $release.isStarred -isnot [bool]) {
            $errors.Add("${name}: 'isStarred' must be true or false.")
        }

        $features = @($release.features)
        if ($features.Count -eq 0) { $errors.Add("${name}: 'features' must list at least one feature.") }
        $index = 0
        foreach ($feature in $features) {
            $index++
            $label = "${name} feature $index"
            if ([string]::IsNullOrWhiteSpace([string]$feature.title)) { $errors.Add("${label}: 'title' is empty.") }
            if ([string]::IsNullOrWhiteSpace([string]$feature.description)) { $errors.Add("${label}: 'description' is empty.") }
            $image = [string]$feature.image
            if ($image -notmatch $script:WhatsNewImageNamePattern) {
                $errors.Add("${label}: image '$image' must be a descriptive kebab-case .png name, such as colorful-icon-style.png.")
                continue
            }
            $imagePath = Join-Path $folder $image
            if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) { $errors.Add("${label}: $image is missing from Assets\WhatsNew."); continue }
            $size = Get-PngSize $imagePath
            if ($null -eq $size) { $errors.Add("${label}: $image is not a PNG file.") }
            elseif ($size.Width -ne $script:WhatsNewImageWidth -or $size.Height -ne $script:WhatsNewImageHeight) {
                $errors.Add("${label}: $image is $($size.Width)x$($size.Height); it must be $($script:WhatsNewImageWidth)x$($script:WhatsNewImageHeight).")
            }
        }
    }

    if (-not $versions.ContainsKey($RequiredVersion)) {
        $errors.Add("No release notes for $RequiredVersion. Expected Assets\WhatsNew\$RequiredVersion.json.")
    }
    return $errors.ToArray()
}

if ($MyInvocation.InvocationName -ne '.') {
    $validationErrors = @(Get-WhatsNewValidationErrors $RepositoryRoot $Version)
    if ($validationErrors.Count -gt 0) {
        $validationErrors | ForEach-Object { Write-Host "FAIL $_" -ForegroundColor Red }
        exit 1
    }
    $checked = if ($Version) { $Version } else { Get-WhatsNewManifestVersion ([IO.Path]::GetFullPath($RepositoryRoot)) }
    Write-Host "What's New notes are valid. Required version: $checked." -ForegroundColor Green
}
