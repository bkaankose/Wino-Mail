<#
.SYNOPSIS
Builds the notification button icons and the extra attribution logo sizes.

.DESCRIPTION
Windows shows toast button icons as 16x16 px images at 100% scale. Every icon gets
the same set of resource folders under src/Wino.Mail.WinUI/Assets/NotificationIcons:

  theme-dark      white glyph
  theme-light     black glyph
  contrast-black  white glyph (High Contrast with a dark background)
  contrast-white  black glyph (High Contrast with a light background)
  scale-*         white glyph, the neutral fallback Microsoft documents

Icons listed in $SvgIcons are rendered from icons/svg. The other icons have no SVG
source in the repository; their theme-dark and theme-light PNGs are the masters and
the contrast and neutral variants are copied from them.

With -AttributionLogos the script also writes the Square44x44Logo target sizes that
Windows asks for and the app entries do not ship yet, downscaled from targetsize-256.

.EXAMPLE
pwsh -File scripts/maintenance/build-notification-icons.ps1
pwsh -File scripts/maintenance/build-notification-icons.ps1 -AttributionLogos
#>
[CmdletBinding()]
param(
    [switch]$AttributionLogos
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$iconRoot = Join-Path $repoRoot 'src\Wino.Mail.WinUI\Assets\NotificationIcons'
$svgRoot = Join-Path $repoRoot 'icons\svg'
$appEntryRoot = Join-Path $repoRoot 'src\Wino.Mail.WinUI\Assets\AppEntries'

# Notification icon name -> icons/svg source.
$SvgIcons = [ordered]@{
    'open'          = 'Open'
    'account-fix'   = 'Wrench'
    'calendar-join' = 'EventJoinOnline'
}

$Scales = [ordered]@{ 100 = 16; 125 = 20; 150 = 24; 200 = 32; 400 = 64 }
$White = [System.Windows.Media.Colors]::White
$Black = [System.Windows.Media.Colors]::Black

function Save-Png([System.Windows.Media.Imaging.BitmapSource]$Bitmap, [string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Read-Png([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        return $decoder.Frames[0]
    }
    finally { $stream.Dispose() }
}

function Get-SvgPathData([string]$SvgPath) {
    [xml]$svg = Get-Content -Raw $SvgPath
    $paths = @($svg.svg.path)
    if ($paths.Count -ne 1 -or $svg.svg.viewBox -ne '0 0 1024 1024') {
        throw "$SvgPath must hold one path on a 1024 viewBox."
    }

    # SVG fills with the nonzero rule; WPF's mini-language defaults to even-odd unless F1 is given.
    return "F1 $($paths[0].d)"
}

function New-GlyphBitmap([string]$PathData, [int]$Size, [System.Windows.Media.Color]$Color) {
    $geometry = [System.Windows.Media.Geometry]::Parse($PathData)
    $scale = $Size / 1024.0
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $context.DrawGeometry([System.Windows.Media.SolidColorBrush]::new($Color), $null, $geometry)
    $context.Pop()
    $context.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

foreach ($icon in $SvgIcons.GetEnumerator()) {
    $pathData = Get-SvgPathData (Join-Path $svgRoot "$($icon.Value).svg")

    foreach ($scale in $Scales.GetEnumerator()) {
        Save-Png (New-GlyphBitmap $pathData $scale.Value $White) (Join-Path $iconRoot "theme-dark\scale-$($scale.Key)\$($icon.Key).png")
        Save-Png (New-GlyphBitmap $pathData $scale.Value $Black) (Join-Path $iconRoot "theme-light\scale-$($scale.Key)\$($icon.Key).png")
    }
}

# theme-dark and theme-light are the masters for every icon, rendered or hand-made.
$variants = [ordered]@{
    'contrast-black' = 'theme-dark'
    'contrast-white' = 'theme-light'
    ''               = 'theme-dark'
}

$iconNames = Get-ChildItem (Join-Path $iconRoot 'theme-dark\scale-100') -Filter *.png | ForEach-Object BaseName

foreach ($name in $iconNames) {
    foreach ($scale in $Scales.Keys) {
        foreach ($theme in 'theme-dark', 'theme-light') {
            $master = Join-Path $iconRoot "$theme\scale-$scale\$name.png"
            if (-not (Test-Path $master)) { throw "Missing $master" }
        }

        foreach ($variant in $variants.GetEnumerator()) {
            $source = Join-Path $iconRoot "$($variant.Value)\scale-$scale\$name.png"
            $folder = if ($variant.Key) { "$($variant.Key)\scale-$scale" } else { "scale-$scale" }
            $target = Join-Path $iconRoot "$folder\$name.png"
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item $source $target -Force
        }
    }
}

Write-Host "Notification icons: $($iconNames.Count) icons x $($Scales.Count) scales x 5 variants."

if (-not $AttributionLogos) {
    return
}

# Windows asks for these Square44x44Logo target sizes; the app entries ship 16, 24, 32, 48 and 256.
$targetSizes = 20, 30, 36, 40, 60, 64, 72, 80, 96
$logoForms = 'targetsize-{0}', 'altform-unplated_targetsize-{0}', 'altform-lightunplated_targetsize-{0}'

foreach ($entry in Get-ChildItem $appEntryRoot -Directory -Filter *Assets) {
    foreach ($form in $logoForms) {
        $source = Join-Path $entry.FullName ("Square44x44Logo.$form.png" -f 256)
        if (-not (Test-Path $source)) { throw "Missing $source" }
        $master = Read-Png $source

        foreach ($size in $targetSizes) {
            $visual = [System.Windows.Media.DrawingVisual]::new()
            $context = $visual.RenderOpen()
            [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::Fant)
            $context.DrawImage($master, [System.Windows.Rect]::new(0, 0, $size, $size))
            $context.Close()
            $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
            $bitmap.Render($visual)

            Save-Png $bitmap (Join-Path $entry.FullName ("Square44x44Logo.$form.png" -f $size))
        }
    }
}

Write-Host "Attribution logos: $($targetSizes.Count) target sizes x $($logoForms.Count) forms per app entry."
