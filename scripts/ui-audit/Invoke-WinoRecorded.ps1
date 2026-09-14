#requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$RunDirectory,
 [Parameter(Mandatory)][string]$ScenarioId,
 [Parameter(Mandatory)][string[]]$ArgumentList,
 [string]$NextAction='Inspect result before next command.',
 [ValidateRange(1,3600)][int]$TimeoutSeconds=120
)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
if(-not (Test-Path -LiteralPath (Join-Path $run 'run.json'))){throw 'Initialize a run with New-WinoAuditRun.ps1 first.'}
$config=Get-Content -LiteralPath (Join-Path $run 'run.json') -Raw | ConvertFrom-Json
if($config.schemaVersion -ne 1){throw 'Unsupported run schema.'}
if(Test-Path -LiteralPath (Join-Path $run 'PAUSE')){throw 'Audit paused. Reconcile the checkpoint before resuming.'}
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($run)))
$mutex=[Threading.Mutex]::new($false,"Local\WinoAudit-$hash")
$locked=$false
$process=$null
$launched=$false
try {
 try {$locked=$mutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$locked=$true}
 if(-not $locked){throw 'Another recorded command is running for this audit.'}
 $id=[Guid]::NewGuid().ToString('N')
 $started=[DateTimeOffset]::Now
 $exe=(Get-Command winapp -CommandType Application -ErrorAction Stop).Source
 $record=[ordered]@{schemaVersion=1;id=$id;scenario=$ScenarioId;started=$started.ToString('o');executable=$exe;arguments=@($ArgumentList);workingDirectory=(Get-Location).Path;state='started';exitCode=$null;stdout="commands/$id.stdout.txt";stderr="commands/$id.stderr.txt";nextAction=$NextAction}
 $record | ConvertTo-Json -Depth 8 -Compress | Add-Content -LiteralPath (Join-Path $run 'commands.jsonl')
 $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'checkpoint.json')
 @"
# Command checkpoint
Scenario: $ScenarioId
Command ID: $id
State: started; execution may be in flight.
Arguments: $(ConvertTo-Json -InputObject @($ArgumentList) -Compress)
Next: $NextAction

If interrupted, inspect current windows and run-specific records before retrying. Do not automatically repeat a mutation.
"@ | Set-Content -LiteralPath (Join-Path $run 'checkpoint.md')
 $info=[Diagnostics.ProcessStartInfo]::new()
 $info.FileName=$exe
 $info.UseShellExecute=$false
 $info.CreateNoWindow=$true
 $info.RedirectStandardOutput=$true
 $info.RedirectStandardError=$true
 foreach($arg in $ArgumentList){$info.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::new();$process.StartInfo=$info
 $null=$process.Start()
 $launched=$true
 $stdout=$process.StandardOutput.ReadToEndAsync()
 $stderr=$process.StandardError.ReadToEndAsync()
 $state='completed'
 while(-not $process.WaitForExit(200)){
  if(Test-Path -LiteralPath (Join-Path $run 'PAUSE')){$state='interrupted';$process.Kill($true);break}
  if(([DateTimeOffset]::Now-$started).TotalSeconds -gt $TimeoutSeconds){$state='timed-out';$process.Kill($true);break}
 }
 $process.WaitForExit()
 $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
 $out | Set-Content -LiteralPath (Join-Path $run $record.stdout)
 $err | Set-Content -LiteralPath (Join-Path $run $record.stderr)
 $record.state=$state;$record.exitCode=$process.ExitCode
 $record['finished']=[DateTimeOffset]::Now.ToString('o')
 $record | ConvertTo-Json -Depth 8 -Compress | Add-Content -LiteralPath (Join-Path $run 'commands.jsonl')
 $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'checkpoint.json')
 @"
# Command checkpoint
Scenario: $ScenarioId
Command ID: $id
State: $state
Exit code: $($record.exitCode)
Next: $NextAction

Exact arguments and output paths: checkpoint.json. Test records: records.json. Scenario outcomes: results.jsonl.
Completion means CLI execution ended, not that a scenario passed. Resolve interruptions by inspecting before retrying.
"@ | Set-Content -LiteralPath (Join-Path $run 'checkpoint.md')
 if($out){Write-Output $out.TrimEnd()}
 if($state -ne 'completed' -or $record.exitCode -ne 0){throw "WinApp $state, exit $($record.exitCode). See command $id outputs. $err"}
} finally {
 if($process){if($launched -and -not $process.HasExited){$process.Kill($true)};$process.Dispose()}
 if($locked){$mutex.ReleaseMutex()}
 $mutex.Dispose()
}
