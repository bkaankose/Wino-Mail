# Read-only recorder acceptance checks. Does not launch or mutate Wino.
#requires -Version 7.0
$ErrorActionPreference='Stop'
$run=& "$PSScriptRoot/New-WinoAuditRun.ps1"
$recorder="$PSScriptRoot/Invoke-WinoRecorded.ps1"
& $recorder -RunDirectory $run -ScenarioId RECORDER-VERSION -ArgumentList @('--version') | Out-Null
$argsToPreserve=@('--this-option-does-not-exist','literal argument with spaces',"apostrophe's",'literal $value; no execution')
$failed=$false
try { & $recorder -RunDirectory $run -ScenarioId RECORDER-ARGUMENTS -ArgumentList $argsToPreserve | Out-Null } catch {$failed=$true}
if(-not $failed){throw 'Expected CLI error did not propagate.'}
$events=@(Get-Content "$run/commands.jsonl" | ForEach-Object {$_ | ConvertFrom-Json})
$entry=$events | Where-Object {$_.scenario -eq 'RECORDER-ARGUMENTS' -and $_.state -eq 'completed'} | Select-Object -Last 1
if(-not $entry -or $entry.exitCode -eq 0){throw 'CLI failure not recorded.'}
if((ConvertTo-Json -InputObject @($entry.arguments) -Compress) -cne (ConvertTo-Json -InputObject $argsToPreserve -Compress)){throw 'Argument boundaries changed.'}
if(-not (Test-Path (Join-Path $run $entry.stdout)) -or -not (Test-Path (Join-Path $run $entry.stderr))){throw 'Output artifact missing.'}
$count=(Get-Content "$run/commands.jsonl").Count
New-Item "$run/PAUSE" -ItemType File | Out-Null
$paused=$false
try { & $recorder -RunDirectory $run -ScenarioId RECORDER-PAUSE -ArgumentList @('--version') | Out-Null } catch {$paused=$_.Exception.Message -like 'Audit paused*'}
if(-not $paused -or (Get-Content "$run/commands.jsonl").Count -ne $count){throw 'Paused run dispatched or recorded another command.'}
Remove-Item -LiteralPath "$run/PAUSE"
'PASS: exact arguments, CLI error/output capture, and pause-before-dispatch.' | Set-Content "$run/recorder-validation.txt"
Write-Output "PASS. Recorder verification artifacts: $run"
