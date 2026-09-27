#requires -Version 7.0
[CmdletBinding()]
param(
 [string]$OutputRoot=(Join-Path $PSScriptRoot '../../artifacts/ui-audit'),
 [string[]]$Accounts=@()
)
$ErrorActionPreference='Stop'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$suffix=[Guid]::NewGuid().ToString('N').Substring(0,6)
$run=[IO.Path]::GetFullPath((Join-Path $OutputRoot "$stamp-$suffix"))
New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path (Join-Path $run 'commands') | Out-Null
$config=[ordered]@{schemaVersion=1;created=[DateTimeOffset]::Now.ToString('o');prefix="WinoRegression-$stamp-$suffix";accounts=@($Accounts);packageName='58272BurakKSE.WinoMailPreview';publisher='CN=51FBDAF3-E212-4149-89A2-A2636B3BC911';packageFamily='58272BurakKSE.WinoMailPreview_mhdqskaa8n2sj';state='prepared';execution='scripted-regression';contract='REGRESSION.md'}
$config | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'run.json')
'[]' | Set-Content (Join-Path $run 'records.json')
'' | Set-Content (Join-Path $run 'results.jsonl')
@"
# Checkpoint
State: prepared; no app launched or data changed.
Next: Run-WinoRegression.ps1 performs preflight and records its WinApp commands.
Run prefix: $($config.prefix)
Accounts: $($Accounts -join ', ')
"@ | Set-Content (Join-Path $run 'checkpoint.md')
Write-Output $run
