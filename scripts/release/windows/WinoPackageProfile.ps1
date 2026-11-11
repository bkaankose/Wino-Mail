<#
.SYNOPSIS
Apply a release profile (identity, display names, COM class IDs) to a Wino package manifest.
.DESCRIPTION
build-releases.ps1 dot-sources this file for the sideload channels. Debug builds of Wino.Mail.WinUI run it
with -ManifestPath, -ProfilePath and -OutputPath to give the development package its own identity.
It stays compatible with Windows PowerShell 5.1 because Visual Studio builds may not have PowerShell 7.
#>
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$ProfilePath,
    [string]$OutputPath
)

function Get-WidgetDefinitionMode {
    param([string]$DefinitionId)

    switch ($DefinitionId) {
        'Wino_Mail' { 'Mail' }
        'Wino_Calendar' { 'Calendar' }
        'Wino_People' { 'People' }
        'Wino_ToDo' { 'Tasks' }
        default { throw "Unknown widget definition: $DefinitionId" }
    }
}

function Set-WinoManifestProfile {
    param([xml]$Manifest, [object]$Profile)

    $Manifest.Package.Properties.DisplayName = $Profile.DisplayNames.Mail
    $entries = @{ App = 'Mail'; CalendarApp = 'Calendar'; ContactsApp = 'People'; ToDoApp = 'Tasks' }
    foreach ($app in $Manifest.Package.Applications.Application) {
        $mode = $entries[[string]$app.Id]
        if (-not $mode) { throw "Unknown packaged application: $($app.Id)" }
        $name = $Profile.DisplayNames.$mode
        $visual = $app.SelectSingleNode("*[local-name()='VisualElements']")
        $visual.SetAttribute('DisplayName', $name)
        foreach ($startup in $app.SelectNodes(".//*[local-name()='StartupTask']")) { $startup.SetAttribute('DisplayName', "$name Startup Service") }
        foreach ($node in $app.SelectNodes(".//*[local-name()='ToastNotificationActivation']")) { $node.SetAttribute('ToastActivatorCLSID', $Profile.NotificationActivatorIds.$mode) }
        foreach ($node in $app.SelectNodes(".//*[local-name()='WidgetProvider']/*[local-name()='Activation']/*[local-name()='CreateInstance']")) { $node.SetAttribute('ClassId', $Profile.WidgetProviderId) }
        foreach ($extension in $app.SelectNodes(".//*[local-name()='AppExtension'][@Name='com.microsoft.windows.widgets']")) { $extension.SetAttribute('DisplayName', $name) }
        foreach ($definition in $app.SelectNodes(".//*[local-name()='WidgetProvider']//*[local-name()='Definition']")) {
            $widgetName = $Profile.DisplayNames.(Get-WidgetDefinitionMode $definition.GetAttribute('Id'))
            $definition.SetAttribute('DisplayName', $widgetName)
            foreach ($screenshot in $definition.SelectNodes(".//*[local-name()='Screenshot']")) { $screenshot.SetAttribute('DisplayAltText', $widgetName) }
        }
        foreach ($server in $app.SelectNodes(".//*[local-name()='ExeServer']")) {
            # The application itself hosts the session-start background task, Wino.WidgetHost the widget provider;
            # every other server is the notification host.
            $executable = $server.GetAttribute('Executable')
            $displayName, $classId = switch ($executable) {
                'Wino.Mail.WinUI.exe' { "$name startup service", $Profile.StartupTaskActivatorId }
                'Wino.WidgetHost.exe' { "$name widget provider", $Profile.WidgetProviderId }
                default { "$name notification host", $Profile.NotificationActivatorIds.$mode }
            }
            $server.SetAttribute('DisplayName', $displayName)
            foreach ($node in $server.SelectNodes("*[local-name()='Class']")) {
                $node.SetAttribute('Id', $classId)
                $node.SetAttribute('DisplayName', $displayName)
            }
        }
    }
}

if ($ManifestPath) {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    if (-not $ProfilePath -or -not $OutputPath) { throw 'ProfilePath and OutputPath are required with ManifestPath.' }

    $manifest = New-Object xml
    $manifest.PreserveWhitespace = $true
    $manifest.LoadXml([IO.File]::ReadAllText($ManifestPath, [Text.Encoding]::UTF8))
    $releaseProfile = [IO.File]::ReadAllText($ProfilePath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $manifest.Package.Identity.Name = [string]$releaseProfile.PackageName
    $manifest.Package.Identity.Publisher = [string]$releaseProfile.Publisher
    Set-WinoManifestProfile $manifest $releaseProfile

    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
    [void][IO.Directory]::CreateDirectory($directory)
    $manifest.Save($OutputPath)
}
