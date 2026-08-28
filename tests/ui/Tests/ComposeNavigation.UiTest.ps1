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

Invoke-UiStep "Click New Mail" {
    winapp ui click "XBindDomainTranslatorMenuNewMailModeOneTime" -a $AppPid
}

Invoke-UiStep "Verify ComposePage opened" {
    winapp ui wait-for "AccountsComboBox" -a $AppPid --timeout 20000
}

$screenshotPath = Join-Path $ArtifactsPath "compose-page.png"
Invoke-UiStep "Capture ComposePage" {
    winapp ui screenshot -a $AppPid -o $screenshotPath --json | Out-Null
}

Invoke-UiStep "Return to the mail list" {
    winapp ui click "ComposePageAppBarButton3" -a $AppPid

    if (Test-ElementVisible "PrimaryButton" 3000) {
        winapp ui click "PrimaryButton" -a $AppPid
    }
}
Assert-ElementGone "AccountsComboBox" 10000
Assert-ElementVisible "MailListView" 5000
