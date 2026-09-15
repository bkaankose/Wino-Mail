#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../scripts/ui-audit/Regression.Common.psm1') -Force -DisableNameChecking
$root = Join-Path ([IO.Path]::GetTempPath()) ('wino-regression-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
'[]' | Set-Content (Join-Path $root 'records.json')
$script:passed = 0
$state = @{ Calls=[System.Collections.Generic.List[object]]::new(); Output='{}'; Failure=$false }
$invoke = {
    param([string[]]$Arguments, [string]$Scenario)
    $state.Calls.Add(@{ Arguments=$Arguments; Scenario=$Scenario })
    if ($state.Failure) { throw 'Simulated CLI failure' }
    $state.Output
}.GetNewClosure()
$context = @{ RunDirectory=$root; Prefix='WinoAudit-test'; Account='Test'; Scenario='TEST'; Window='123'; ProcessId=1; Invoke=$invoke }
Initialize-WinoRegression $context
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { Assert-True ($_.Exception.Message -match $Pattern) "Unexpected failure: $_"; return }
    throw "Expected failure: $Pattern"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    $state.Calls.Clear(); $state.Output='{}'; $state.Failure=$false
    & $Action; $script:passed++; Write-Host "PASS $Name"
}
Test-Case 'UI values retain argument boundaries and exact window' {
    Invoke-RegressionUi @('set-value','Selector','spaces; $literal') | Out-Null
    $call = $state.Calls[0].Arguments
    Assert-True ($call.Count -eq 7 -and $call[3] -ceq 'spaces; $literal' -and $call[5] -eq '123') 'Arguments changed.'
}
Test-Case 'Failed gone assertion remains failure' {
    $state.Failure=$true
    Assert-Throws { Wait-RegressionElement Missing -Gone } 'Simulated CLI failure'
    Assert-True ($state.Calls.Count -eq 1) 'Failed assertion retried.'
}
Test-Case 'Pause prevents any dispatch' {
    New-Item -ItemType File -Path (Join-Path $root 'PAUSE') | Out-Null
    Assert-Throws { Invoke-RegressionUi @('invoke','Delete') } 'Audit paused'
    Assert-True ($state.Calls.Count -eq 0) 'Paused mutation dispatched.'
    Remove-Item -LiteralPath (Join-Path $root 'PAUSE')
}
Test-Case 'Multiple main windows fail without selecting the first' {
    $state.Output='[{"label":"window","ownerHwnd":0,"hwnd":1,"processId":2},{"label":"window","ownerHwnd":0,"hwnd":3,"processId":2}]'
    Assert-Throws { Update-RegressionWindow } 'Expected one Wino main window'
}
Test-Case 'Popup window does not make main window ambiguous' {
    $state.Output='[{"label":"window","ownerHwnd":0,"hwnd":123,"processId":2},{"label":"window","ownerHwnd":123,"hwnd":3,"processId":2}]'
    Assert-True ((Update-RegressionWindow).hwnd -eq 123) 'Wrong window selected.'
}
Test-Case 'Duplicate selectors fail' {
    $state.Output='{"windows":[{"elements":[{"name":"A","automationId":"Row","type":"Text","isOffscreen":false,"selector":"x"},{"name":"A","automationId":"Row","type":"Text","isOffscreen":false,"selector":"y"}]}]}'
    Assert-Throws { Find-RegressionElement -Name A -AutomationId Row } 'Found 2'
}
Test-Case 'Scoped selection preserves the current dynamic selector' {
    $state.Output='{"windows":[{"elements":[{"name":"A","automationId":"Row","type":"Text","isOffscreen":false,"selector":"fresh-id"}]}]}'
    Assert-True ((Find-RegressionElement -Name A -AutomationId Row -Scope NavigationView).selector -eq 'fresh-id') 'Wrong selector.'
    Assert-True ($state.Calls[0].Arguments -contains 'NavigationView') 'Inspection was not scoped.'
}
Test-Case 'Names outside this run cannot enter the mutation ledger' {
    Assert-Throws { Set-RegressionRecord contact 'Existing user contact' intended } 'Only this run prefix'
    Assert-True (@(Get-Content (Join-Path $root 'records.json') -Raw | ConvertFrom-Json).Count -eq 0) 'Foreign record entered ledger.'
}
Test-Case 'Intended state survives failure and can be reconciled explicitly' {
    Set-RegressionRecord contact 'WinoAudit-test-Contact' intended
    $state.Failure=$true
    Assert-Throws { Invoke-RegressionUi @('invoke','ContactEditorSave') } 'Simulated CLI failure'
    $record = @(Get-Content (Join-Path $root 'records.json') -Raw | ConvertFrom-Json)[0]
    Assert-True ($record.state -eq 'intended') 'Uncertain mutation became confirmed.'
    Assert-True ($state.Calls.Count -eq 1) 'Mutation retried automatically.'
    Set-RegressionRecord contact 'WinoAudit-test-Contact' confirmed
    Set-RegressionRecord contact 'WinoAudit-test-Contact' removed
    $record = @(Get-Content (Join-Path $root 'records.json') -Raw | ConvertFrom-Json)
    Assert-True ($record.Count -eq 1 -and $record[0].state -eq 'removed') 'Record transition duplicated or failed.'
}
Write-Host "$passed regression helper checks passed. No application commands ran. Fixtures: $root"
