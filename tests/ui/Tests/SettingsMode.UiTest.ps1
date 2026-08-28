[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(0, 10000)][int]$StepDelayMilliseconds = 700
)

. (Join-Path $PSScriptRoot "UiTest.Common.ps1")

Invoke-Mode "Settings"
Invoke-UiStep "Verify Settings home is visible" {
    winapp ui wait-for "SettingOptionsPageComboBox" -a $AppPid --timeout 10000
}

$screenshotPath = Join-Path $ArtifactsPath "settings-mode.png"
Invoke-UiStep "Capture Settings mode" {
    winapp ui screenshot -a $AppPid -o $screenshotPath --json | Out-Null
}
