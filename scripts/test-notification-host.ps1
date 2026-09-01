param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Mail', 'Calendar', 'People', 'Tasks')]
    [string]$Application,

    [string]$Title = 'Wino notification host test',

    [string]$Body = 'This notification was published by a dedicated Wino host identity.',

    [string]$Tag = 'wino-notification-host-diagnostic',

    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot '..\src\Wino.Mail.WinUI\Package.appxmanifest'
$manifest = [xml](Get-Content -LiteralPath $manifestPath)
$package = Get-AppxPackage -Name $manifest.Package.Identity.Name

if ($null -eq $package) {
    throw 'The Wino Debug package is not installed.'
}

$applicationMap = @{
    Mail = @{ Id = 'MailNotificationHost'; Value = [byte]1 }
    Calendar = @{ Id = 'CalendarNotificationHost'; Value = [byte]2 }
    People = @{ Id = 'PeopleNotificationHost'; Value = [byte]3 }
    Tasks = @{ Id = 'ToDoNotificationHost'; Value = [byte]4 }
}

$target = $applicationMap[$Application]
$requestId = [Guid]::NewGuid()
$localCachePath = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache"
$requestDirectory = Join-Path $localCachePath 'NotificationHost\Requests'
$finalPath = Join-Path $requestDirectory "$($requestId.ToString('N')).bin"
$temporaryPath = "$finalPath.tmp"
$escapedTitle = [System.Security.SecurityElement]::Escape($Title)
$escapedBody = [System.Security.SecurityElement]::Escape($Body)
$payload = "<toast><visual><binding template=`"ToastGeneric`"><text>$escapedTitle</text><text>$escapedBody</text></binding></visual><audio src=`"ms-winsoundevent:Notification.Default`" /></toast>"

[System.IO.Directory]::CreateDirectory($requestDirectory) | Out-Null

$stream = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($stream, [System.Text.Encoding]::UTF8, $false)

try {
    $writer.Write([uint32]0x4F484E57)
    $writer.Write([uint16]1)
    $writer.Write([byte]1)
    $writer.Write([DateTimeOffset]::UtcNow.Ticks)
    $writer.Write([byte]($Remove ? 2 : 1))
    $writer.Write($target.Value)

    if ($Remove) {
        $writer.Write([int]-1)
    }
    else {
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $writer.Write([int]$payloadBytes.Length)
        $writer.Write($payloadBytes)
    }

    $tagBytes = [System.Text.Encoding]::UTF8.GetBytes($Tag)
    $writer.Write([int]$tagBytes.Length)
    $writer.Write($tagBytes)
    $writer.Write([int]-1)
}
finally {
    $writer.Dispose()
}

[System.IO.File]::Move($temporaryPath, $finalPath)

if ($null -eq ('WinoNotificationHostActivationManager' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinoNotificationHostActivationManager
{
    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(string appUserModelId, string arguments, uint options, out uint processId);
        int ActivateForFile(IntPtr itemArray, string verb, out uint processId);
        int ActivateForProtocol(IntPtr itemArray, out uint processId);
    }

    public static uint Activate(string appUserModelId, string arguments)
    {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type);
        var result = manager.ActivateApplication(appUserModelId, arguments, 0, out var processId);
        Marshal.ThrowExceptionForHR(result);
        return processId;
    }
}
'@
}

try {
    $appUserModelId = "$($package.PackageFamilyName)!$($target.Id)"
    $processId = [WinoNotificationHostActivationManager]::Activate($appUserModelId, "--request $($requestId.ToString('D'))")
    [pscustomobject]@{
        Application = $Application
        Operation = $Remove ? 'RemoveByTag' : 'Show'
        AppUserModelId = $appUserModelId
        RequestId = $requestId
        ProcessId = $processId
    }
}
catch {
    if (Test-Path -LiteralPath $finalPath) {
        Remove-Item -LiteralPath $finalPath -Force
    }

    throw
}
