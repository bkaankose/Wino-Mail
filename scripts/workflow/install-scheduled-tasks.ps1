<#
.SYNOPSIS
Registers the Windows Task Scheduler jobs of the unattended workflow for the current user.

.DESCRIPTION
Creates or updates three tasks that run only while the user is logged on, because the workers need the interactive desktop:
  WinoWork-Tick   every 15 minutes: work.ps1 tick
  WinoWork-Brief  daily at the configured brief time (default 07:30): work.ps1 brief
  WinoWork-Boot   at logon: work.ps1 boot, then the coordinator session in a PowerShell window (Task Scheduler cannot start the wt.exe Store alias)

.PARAMETER Remove
Unregisters the three tasks.

.PARAMETER BriefTime
Local time of the daily brief, HH:mm. Default 07:30.

.EXAMPLE
pwsh -File scripts/workflow/install-scheduled-tasks.ps1
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [string]$BriefTime = '07:30'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workScript = Join-Path $repoRoot 'scripts\workflow\work.ps1'
$pwsh = (Get-Command pwsh).Source
$names = @('WinoWork-Tick', 'WinoWork-Brief', 'WinoWork-Boot')

if ($Remove) {
    foreach ($name in $names) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            Write-Host "Removed $name"
        }
    }
    return
}

if (-not (Test-Path $workScript)) { throw "Missing $workScript" }

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)

function Register-WorkTask {
    param([string]$Name, [string]$Description, $Trigger, $Action)
    Register-ScheduledTask -TaskName $Name -Description $Description -Trigger $Trigger -Action $Action -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Registered $Name"
}

$tick = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -File `"$workScript`" tick" -WorkingDirectory $repoRoot
$tickTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 15)
Register-WorkTask -Name 'WinoWork-Tick' -Description 'Wino workflow: reap workers, enforce build timeout, prune worktrees, ping the Mac, flush notifications' -Trigger $tickTrigger -Action $tick

$brief = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -File `"$workScript`" brief" -WorkingDirectory $repoRoot
$briefTrigger = New-ScheduledTaskTrigger -Daily -At $BriefTime
Register-WorkTask -Name 'WinoWork-Brief' -Description 'Wino workflow: morning brief to Discord' -Trigger $briefTrigger -Action $brief

$startCoordinator = Join-Path $repoRoot 'scripts\workflow\start-coordinator.ps1'
$boot = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoExit -NoProfile -File `"$startCoordinator`"" -WorkingDirectory $repoRoot
$bootTrigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$bootTrigger.Delay = 'PT2M'
Register-WorkTask -Name 'WinoWork-Boot' -Description 'Wino workflow: crash recovery alert and the coordinator session' -Trigger $bootTrigger -Action $boot

Write-Host "Tasks run as $env:USERNAME only while logged on. Start the coordinator now with: Start-ScheduledTask -TaskName WinoWork-Boot"
