[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(0, 10000)][int]$StepDelayMilliseconds = 700
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-UiStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    Write-Host "  $Name"
    $global:LASTEXITCODE = 0
    & $Action

    if ($LASTEXITCODE -ne 0) {
        throw "UI step failed: $Name"
    }

    if ($StepDelayMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $StepDelayMilliseconds
    }
}

function Assert-ElementGone {
    param(
        [Parameter(Mandatory)][string]$AutomationId,
        [int]$TimeoutMilliseconds = 3000
    )

    $deadline = (Get-Date).AddMilliseconds($TimeoutMilliseconds)

    do {
        if (-not (Test-ElementVisible $AutomationId 150)) {
            $global:LASTEXITCODE = 0
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "'$AutomationId' remained visible after $TimeoutMilliseconds ms."
}

function Test-ElementVisible {
    param(
        [Parameter(Mandatory)][string]$AutomationId,
        [int]$TimeoutMilliseconds = 750
    )

    $previousErrorActionPreference = $ErrorActionPreference

    try {
        $ErrorActionPreference = "Continue"
        & winapp ui wait-for $AutomationId -a $AppPid --timeout $TimeoutMilliseconds *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$initialScreenshot = Join-Path $ArtifactsPath "startup-initial.png"
$flyoutScreenshot = Join-Path $ArtifactsPath "startup-account-flyout.png"
$finalScreenshot = Join-Path $ArtifactsPath "startup-final.png"

Invoke-UiStep "Verify that the application responds" {
    winapp ui status -a $AppPid --json | Out-Null
}

Invoke-UiStep "Capture the initial window" {
    winapp ui screenshot -a $AppPid -o $initialScreenshot --json | Out-Null
}

$startupSelectors = @(
    "NavigationView",
    "WelcomePageV2Button",
    "ProviderSelectionPageButton",
    "AccountSetupProgressPageButton",
    "IdlePageButton"
)
$visibleStartupSelector = $startupSelectors | Where-Object { Test-ElementVisible $_ } | Select-Object -First 1

if ($null -eq $visibleStartupSelector) {
    throw "Wino Mail opened, but no recognized startup surface became visible."
}

Write-Host "  Startup surface: $visibleStartupSelector"

if (Test-ElementVisible "WinoAccountButton" 1000) {
    Invoke-UiStep "Reset any open flyout" {
        winapp ui focus "WinoAccountButton" -a $AppPid
        winapp ui send-keys "escape" -a $AppPid --via send-input
        Start-Sleep -Milliseconds 300
    }

    Invoke-UiStep "Open the Wino Account flyout" {
        winapp ui invoke "WinoAccountButton" -a $AppPid
    }

    $flyoutContentSelector = $null

    if (Test-ElementVisible "WinoAccountFlyoutManageAccountButton" 2500) {
        $flyoutContentSelector = "WinoAccountFlyoutManageAccountButton"
        Write-Host "  Account state: signed in"
    }
    elseif (Test-ElementVisible "ShellWindowButton" 2500) {
        $flyoutContentSelector = "ShellWindowButton"
        Write-Host "  Account state: signed out"
    }

    if ($null -eq $flyoutContentSelector) {
        throw "The Wino Account flyout opened without recognized signed-in or signed-out content."
    }

    Invoke-UiStep "Capture the open account flyout" {
        winapp ui screenshot -a $AppPid --capture-screen -o $flyoutScreenshot --json | Out-Null
    }

    Invoke-UiStep "Dismiss the account flyout" {
        winapp ui send-keys "escape" -a $AppPid --via send-input
    }

    Invoke-UiStep "Verify that the account flyout closed" {
        Assert-ElementGone $flyoutContentSelector 3000
    }
}
else {
    Write-Host "  Wino Account button is hidden in this startup state; the flyout interaction was skipped."
}

Invoke-UiStep "Capture the final window" {
    winapp ui screenshot -a $AppPid -o $finalScreenshot --json | Out-Null
}
