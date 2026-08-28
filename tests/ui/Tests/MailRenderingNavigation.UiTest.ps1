[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(0, 10000)][int]$StepDelayMilliseconds = 700
)

. (Join-Path $PSScriptRoot "UiTest.Common.ps1")

Invoke-Mode "Mail"
Invoke-UiStep "Verify the mail list is visible" {
    winapp ui wait-for "MailListView" -a $AppPid --timeout 10000
}

$mailItem = Get-FirstVisibleMailItem
Invoke-UiStep "Click the first visible mail" {
    winapp ui invoke $mailItem.selector -a $AppPid
}

Invoke-UiStep "Verify MailRenderingPage opened" {
    winapp ui wait-for "MailMessageRenderer" -a $AppPid --timeout 10000
}

$screenshotPath = Join-Path $ArtifactsPath "mail-rendering-page.png"
Invoke-UiStep "Capture MailRenderingPage" {
    winapp ui screenshot -a $AppPid -o $screenshotPath --json | Out-Null
}

Invoke-UiStep "Return to the mail list" {
    winapp ui send-keys "escape" -a $AppPid --via send-input
}
Assert-ElementVisible "MailListView" 5000
