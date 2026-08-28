[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(0, 10000)][int]$StepDelayMilliseconds = 700
)

. (Join-Path $PSScriptRoot "UiTest.Common.ps1")

Invoke-Mode "Settings"
Assert-ElementVisible "SettingOptionsPageComboBox" 10000

$currentLanguageResult = & winapp ui get-value "SettingOptionsPageComboBox" -a $AppPid --json
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the current display language."
}

$currentLanguage = (($currentLanguageResult -join [Environment]::NewLine) | ConvertFrom-Json).text
$baselineMenuNames = @(Get-SettingsShellMenuNames)

if ($baselineMenuNames.Count -eq 0) {
    throw "No Settings shell menu items were found before changing language."
}

$languageCandidates = @("Deutsch", "English", "Français", "Polski", "Español", "Türkçe", "中文")
$targetLanguage = $languageCandidates | Where-Object { $_ -ne $currentLanguage } | Select-Object -First 1

if ($null -eq $targetLanguage) {
    throw "Could not select a display language different from '$currentLanguage'."
}

Invoke-UiStep "Open the display language selector" {
    winapp ui invoke "SettingOptionsPageComboBox" -a $AppPid
}

Invoke-UiStep "Change display language from '$currentLanguage' to '$targetLanguage'" {
    $languageSelector = Get-VisibleElementSelectorByName -InspectionSelector "SettingOptionsPageComboBox" -Name $targetLanguage
    winapp ui invoke $languageSelector -a $AppPid
}

Assert-ElementVisible "SettingOptionsPageComboBox" 10000

$deadline = (Get-Date).AddSeconds(10)
$updatedMenuNames = @()
do {
    $updatedMenuNames = @(Get-SettingsShellMenuNames)

    if ($updatedMenuNames.Count -gt 0 -and
        (Compare-Object -ReferenceObject $baselineMenuNames -DifferenceObject $updatedMenuNames)) {
        break
    }

    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

if ($updatedMenuNames.Count -eq 0 -or -not (Compare-Object -ReferenceObject $baselineMenuNames -DifferenceObject $updatedMenuNames)) {
    throw "Settings shell menu labels did not update after changing language to '$targetLanguage'."
}

Write-Host "  Shell menu changed: '$($baselineMenuNames -join ', ')' -> '$($updatedMenuNames -join ', ')'"

try {
    Invoke-UiStep "Restore display language to '$currentLanguage'" {
        winapp ui invoke "SettingOptionsPageComboBox" -a $AppPid
        $languageSelector = Get-VisibleElementSelectorByName -InspectionSelector "SettingOptionsPageComboBox" -Name $currentLanguage
        winapp ui invoke $languageSelector -a $AppPid
    }

    Assert-ElementVisible "SettingOptionsPageComboBox" 10000
}
catch {
    Write-Warning "Could not restore the original display language '$currentLanguage': $($_.Exception.Message)"
}

$screenshotPath = Join-Path $ArtifactsPath "settings-language-updated.png"
Invoke-UiStep "Capture the updated Settings shell" {
    winapp ui screenshot -a $AppPid -o $screenshotPath --json | Out-Null
}
