<#
.SYNOPSIS
Runs the current Mail-Cal development build inside Windows Sandbox.

.DESCRIPTION
Use this when Smart App Control on the host blocks unsigned development builds. The script
maps the built MSIX layout into a fresh Windows Sandbox, enables Developer Mode there,
registers the package and launches it. Logs from the sandboxed app are copied back to
artifacts/sandbox/logs every 15 seconds while the sandbox runs.

Build first, for example:
  dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

The sandbox is discarded when it closes, including any accounts added inside it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$layout = Join-Path $repoRoot "src\MailCal.Mail.WinUI\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\AppX"

if (-not (Test-Path (Join-Path $layout 'AppxManifest.xml'))) {
    throw "No package layout at $layout. Build the app first."
}

if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
    throw 'Windows Sandbox is already running. Close it first; only one sandbox can run at a time.'
}

$work = Join-Path $repoRoot 'artifacts\sandbox'
$boot = Join-Path $work 'boot'
$logs = Join-Path $work 'logs'
New-Item -ItemType Directory -Force -Path $boot, $logs | Out-Null
Get-ChildItem $logs -File -ErrorAction SilentlyContinue | Remove-Item -Force

# Runs inside the sandbox as its administrator account.
$startScript = @'
$ErrorActionPreference = 'Continue'
$log = 'C:\MailCal\logs\sandbox-start.log'
function Write-Log($message) { "$(Get-Date -Format o) $message" | Add-Content -Path $log }

try {
    Write-Log 'Copying package layout.'
    Copy-Item -Path 'C:\MailCal\AppX' -Destination 'C:\MailCalApp' -Recurse -Force

    Write-Log 'Enabling Developer Mode.'
    New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord

    Write-Log 'Registering package.'
    Add-AppxPackage -Register 'C:\MailCalApp\AppxManifest.xml' -ForceApplicationShutdown
    $package = Get-AppxPackage -Name 'hairokuma.MailCal'
    Write-Log "Registered $($package.PackageFullName)."

    Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
    Write-Log 'Launched.'

    $state = "$env:LOCALAPPDATA\Packages\$($package.PackageFamilyName)\LocalState"
    while ($true) {
        Start-Sleep -Seconds 15
        Get-ChildItem $state -Filter *.log -ErrorAction SilentlyContinue | Copy-Item -Destination 'C:\MailCal\logs' -Force
        (Get-Process MailCal.Mail.WinUI -ErrorAction SilentlyContinue | Select-Object Id, Responding, StartTime | Out-String) |
            Set-Content 'C:\MailCal\logs\process.txt'
    }
}
catch {
    Write-Log "FAILED: $($_ | Out-String)"
}
'@
Set-Content -Path (Join-Path $boot 'start.ps1') -Value $startScript -Encoding UTF8

$wsb = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$layout</HostFolder>
      <SandboxFolder>C:\MailCal\AppX</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$boot</HostFolder>
      <SandboxFolder>C:\MailCal\boot</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$logs</HostFolder>
      <SandboxFolder>C:\MailCal\logs</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File C:\MailCal\boot\start.ps1</Command>
  </LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $work 'Mail-Cal.wsb'
Set-Content -Path $wsbPath -Value $wsb -Encoding Ascii

Start-Process $wsbPath
Write-Host "Windows Sandbox is starting. Logs: $logs"
