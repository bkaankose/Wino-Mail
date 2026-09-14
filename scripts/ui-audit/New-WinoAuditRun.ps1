#requires -Version 7.0
[CmdletBinding()]
param(
 [string]$OutputRoot=(Join-Path $PSScriptRoot '../../artifacts/ui-audit'),
 [string[]]$Accounts=@('Personal','Work','Gmail','iCloud'),
 [string]$PhotoPath='C:\Users\bkaan\Pictures\Vesika.png'
)
$ErrorActionPreference='Stop'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$suffix=[Guid]::NewGuid().ToString('N').Substring(0,6)
$run=[IO.Path]::GetFullPath((Join-Path $OutputRoot "$stamp-$suffix"))
New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path (Join-Path $run 'commands'),(Join-Path $run 'evidence'),(Join-Path $run 'fixtures') | Out-Null
Copy-Item -Path "$PSScriptRoot/fixtures/*" -Destination (Join-Path $run 'fixtures')
$config=[ordered]@{schemaVersion=1;created=[DateTimeOffset]::Now.ToString('o');prefix="WinoAudit-$stamp-$suffix";accounts=@($Accounts);photoPath=[IO.Path]::GetFullPath($PhotoPath);packageName='58272BurakKSE.WinoMailPreview';publisher='CN=51FBDAF3-E212-4149-89A2-A2636B3BC911';packageFamily='58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj';baseline='20260913-213623';state='prepared';execution='agent-guided';contract='REPLAY.md'}
$config | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'run.json')
'[]' | Set-Content (Join-Path $run 'records.json')
'' | Set-Content (Join-Path $run 'results.jsonl')
Copy-Item "$PSScriptRoot/REPLAY.md" (Join-Path $run 'REPLAY.md')
Copy-Item "$PSScriptRoot/scenarios.json" (Join-Path $run 'scenarios.json')
@"
# Checkpoint
State: prepared; no app launched or data changed.
Next: follow REPLAY.md preflight. Use Invoke-WinoRecorded.ps1 for EVERY WinApp command, including queries, focus, retries, launch and screenshots.
Run prefix: $($config.prefix)
Accounts: $($Accounts -join ', ')
Photo: $($config.photoPath)
"@ | Set-Content (Join-Path $run 'checkpoint.md')
Write-Output $run
