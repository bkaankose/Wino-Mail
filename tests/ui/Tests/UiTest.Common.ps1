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

function Assert-ElementVisible {
    param(
        [Parameter(Mandatory)][string]$AutomationId,
        [int]$TimeoutMilliseconds = 5000
    )

    if (-not (Test-ElementVisible $AutomationId $TimeoutMilliseconds)) {
        throw "Expected '$AutomationId' to become visible within $TimeoutMilliseconds ms."
    }
}

function Assert-ElementGone {
    param(
        [Parameter(Mandatory)][string]$AutomationId,
        [int]$TimeoutMilliseconds = 5000
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

function Get-UiInspection {
    param(
        [Parameter(Mandatory)][string]$Selector,
        [int]$Depth = 8
    )

    $inspectionOutput = & winapp ui inspect $Selector -a $AppPid --interactive --depth $Depth --json 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $inspectionOutput) {
        throw "Could not inspect UI selector '$Selector'."
    }

    try {
        return (($inspectionOutput -join [Environment]::NewLine) | ConvertFrom-Json)
    }
    catch {
        throw "WinApp returned invalid inspection JSON for '$Selector'."
    }
}

function Expand-UiElements {
    param([Parameter(Mandatory)][object[]]$Elements)

    foreach ($element in $Elements) {
        $element

        if ($element.PSObject.Properties.Name -contains "children" -and $null -ne $element.children) {
            Expand-UiElements -Elements @($element.children)
        }
    }
}

function Get-ModeFooterItems {
    $inspection = Get-UiInspection -Selector "NavigationView"
    $elements = Expand-UiElements -Elements @($inspection.windows | ForEach-Object { $_.elements })

    @($elements |
        Where-Object {
            $_.type -eq "ListItem" -and
            $_.className -eq "ListViewItem" -and
            $_.isInvokable -eq $false -and
            $_.isOffscreen -eq $false -and
            $_.y -gt 900 -and
            $null -ne $_.selector
        } |
        Sort-Object x)
}

function Invoke-Mode {
    param(
        [Parameter(Mandatory)][ValidateSet("Mail", "Settings")][string]$Mode
    )

    $footerItems = @(Get-ModeFooterItems)

    if ($footerItems.Count -lt 2) {
        throw "Could not find the Wino mode footer items."
    }

    $modeItem = if ($Mode -eq "Mail") { $footerItems[0] } else { $footerItems[$footerItems.Count - 1] }
    Invoke-UiStep "Navigate to $Mode mode" {
        winapp ui click $modeItem.selector -a $AppPid
    }
}

function Get-SettingsShellMenuNames {
    $inspection = Get-UiInspection -Selector "NavigationView"
    $elements = Expand-UiElements -Elements @($inspection.windows | ForEach-Object { $_.elements })

    @($elements |
        Where-Object {
            $_.type -eq "ListItem" -and
            $_.PSObject.Properties.Name -contains "automationId" -and
            $_.automationId -eq "DataTemplatesWinoNavigationViewItem3" -and
            $_.isOffscreen -eq $false -and
            $_.isEnabled -eq $true
        } |
        Sort-Object y |
        ForEach-Object { $_.name })
}

function Get-VisibleElementSelectorByName {
    param(
        [Parameter(Mandatory)][string]$InspectionSelector,
        [Parameter(Mandatory)][string]$Name,
        [string]$ElementType = "ListItem"
    )

    $inspection = Get-UiInspection -Selector $InspectionSelector
    $elements = Expand-UiElements -Elements @($inspection.windows | ForEach-Object { $_.elements })
    $element = $elements |
        Where-Object {
            $_.type -eq $ElementType -and
            $_.PSObject.Properties.Name -contains "name" -and
            $_.name -eq $Name -and
            $_.isOffscreen -eq $false -and
            $null -ne $_.selector
        } |
        Select-Object -First 1

    if ($null -eq $element) {
        throw "Could not find visible $ElementType '$Name' in '$InspectionSelector'."
    }

    return $element.selector
}

function Get-FirstVisibleMailItem {
    $inspection = Get-UiInspection -Selector "MailListView"
    $elements = Expand-UiElements -Elements @($inspection.windows | ForEach-Object { $_.elements })

    $mailItem = $elements |
        Where-Object {
            $_.type -eq "ListItem" -and
            $_.className -eq "ListViewItem" -and
            $_.isInvokable -eq $true -and
            $_.isOffscreen -eq $false -and
            $null -ne $_.selector
        } |
        Select-Object -First 1

    if ($null -eq $mailItem) {
        throw "No visible mail item was found in MailListView."
    }

    return $mailItem
}
