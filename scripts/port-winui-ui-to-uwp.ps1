param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$sourceRoot = Join-Path $RepositoryRoot 'Wino.Mail.WinUI'
$targetRoot = Join-Path $RepositoryRoot 'Wino.Mail.Uwp'

if (-not (Test-Path -LiteralPath $sourceRoot) -or -not (Test-Path -LiteralPath $targetRoot)) {
    throw 'Run this script from the Wino repository containing Wino.Mail.WinUI and Wino.Mail.Uwp.'
}

$uiDirectories = @(
    'Assets',
    'AppThemes',
    'BackgroundImages',
    'Behaviors',
    'Controls',
    'Converters',
    'Dialogs',
    'Extensions',
    'Helpers',
    'JS',
    'MenuFlyouts',
    'Models',
    'Selectors',
    'Styles',
    'Views',
    'ViewModels'
)

foreach ($directory in $uiDirectories) {
    $source = Join-Path $sourceRoot $directory
    $target = Join-Path $targetRoot $directory
    if (Test-Path -LiteralPath $source) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
    }
}

$topLevelFiles = @(
    'BasePage.cs',
    'CoreGeneric.xaml',
    'CoreGeneric.xaml.cs',
    'Dispatcher.cs',
    'NotificationArguments.cs'
)

foreach ($file in $topLevelFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination (Join-Path $targetRoot $file) -Force
}

$interfaceFiles = @('ITitleBarSearchHost.cs', 'IWinoFrameProvider.cs')
New-Item -ItemType Directory -Path (Join-Path $targetRoot 'Interfaces') -Force | Out-Null
foreach ($file in $interfaceFiles) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot "Interfaces\$file") -Destination (Join-Path $targetRoot "Interfaces\$file") -Force
}

$excludedServices = @(
    'CalendarReminderServer.cs',
    'HostedContentPopoutCoordinator.cs',
    'MailAuthenticatorConfiguration.cs',
    'NativeTrayIcon.cs',
    'NotificationBuilder.cs',
    'PackagedAppEntryLauncher.cs',
    'PreferencesService.cs',
    'WinoWindowManager.cs'
)

Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Services') -File -Filter '*.cs' |
    Where-Object { $_.Name -notin $excludedServices } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetRoot "Services\$($_.Name)") -Force
    }

# Files tied to desktop windows or unsupported SkiaSharp WinUI views are intentionally
# omitted. The corresponding UI affordances are removed instead of reimplemented through
# a Win32 compatibility layer.
$unsupportedFiles = @(
    'Helpers\WindowAppUserModelIdHelper.cs',
    'Controls\ImagePreviewControl.cs',
    'Styles\ImagePreviewControl.xaml'
)
foreach ($relativePath in $unsupportedFiles) {
    $path = Join-Path $targetRoot $relativePath
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
$portedFiles = Get-ChildItem -LiteralPath $targetRoot -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.xaml' }

foreach ($file in $portedFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $text = $text.Replace('Wino.Mail.WinUI', 'Wino.Mail.Uwp')

    if ($file.Extension -eq '.cs') {
        $text = $text.Replace('Microsoft.UI.Xaml', 'Windows.UI.Xaml')
    }

    [System.IO.File]::WriteAllText($file.FullName, $text, $utf8WithoutBom)
}

Write-Host "Ported $($portedFiles.Count) UI source files into Wino.Mail.Uwp."
