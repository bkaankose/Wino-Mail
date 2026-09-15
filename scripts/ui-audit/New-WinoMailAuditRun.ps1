#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/ui-audit'),
    [string]$AttachmentPath = 'C:\Users\bkaan\Documents\Vesika.png'
)
$ErrorActionPreference = 'Stop'
$prefix = 'WinoMailAudit-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
$run = [IO.Path]::GetFullPath((Join-Path $OutputRoot $prefix))
$null = New-Item -ItemType Directory -Path $run
foreach ($folder in 'commands','evidence','fixtures') { $null = New-Item -ItemType Directory -Path (Join-Path $run $folder) }
@{schemaVersion=1; prefix=$prefix; created=[DateTimeOffset]::Now.ToString('o'); execution='agent-guided'; contract='MAIL-AUDIT.md'; accounts=@(); attachmentPath=$AttachmentPath; originalThreading=$null; theme='unverified'; state='prepared'} |
    ConvertTo-Json -Depth 10 | Set-Content "$run/run.json"
'[]' | Set-Content "$run/records.json"
'' | Set-Content "$run/results.jsonl"
Copy-Item "$PSScriptRoot/mail-scenarios.json" "$run/scenarios.json"
Copy-Item "$PSScriptRoot/MAIL-AUDIT.md" "$run/MAIL-AUDIT.md"
'Prepared. Next: record preflight, deploy current Debug source, then discover accounts through UI. No mail has been changed.' | Set-Content "$run/checkpoint.md"
$run
