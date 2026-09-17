#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('TasksPersistence','ContactsPersistence','ActivationRouting')]
    [string[]]$Scenario = @('TasksPersistence','ContactsPersistence','ActivationRouting'),
    [string]$Account,
    [string]$ContactDestination,
    [ValidateSet('Light','Dark','HighContrast')][string]$Theme,
    [switch]$Restore,
    [switch]$List
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($List) {
    'TasksPersistence: isolated list/task/step, edit, restart, delete and restart'
    'ContactsPersistence: explicit destination, save/edit/restart, discard and delete'
    'ActivationRouting: People to Calendar via empty webcal and webcals'
    return
}
if (($Scenario -contains 'TasksPersistence' -or $Scenario -contains 'ContactsPersistence') -and -not $Account) {
    throw 'Specify -Account using the exact visible account name. No account is selected implicitly.'
}
if ($Scenario -contains 'ContactsPersistence' -and -not $ContactDestination) {
    throw 'Specify -ContactDestination using the exact destination label in the contact picker.'
}
if (-not $Theme) { throw 'Specify the current -Theme for the run record. The report marks it as caller-reported.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repositoryRoot 'src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj'
. (Join-Path $repositoryRoot 'scripts/Wino.Debug.ps1')
Import-Module (Join-Path $PSScriptRoot 'Regression.Common.psm1') -Force -DisableNameChecking
$originalLocation = Get-Location
Set-Location $repositoryRoot
$run = & (Join-Path $PSScriptRoot 'New-WinoAuditRun.ps1') -Accounts @($Account)
$config = Get-Content (Join-Path $run 'run.json') -Raw | ConvertFrom-Json
$config.execution = 'scripted-regression'
$config | Add-Member -NotePropertyName selectedScenarios -NotePropertyValue $Scenario
$config | Add-Member -NotePropertyName contactDestination -NotePropertyValue $ContactDestination
$config | Add-Member -NotePropertyName reportedTheme -NotePropertyValue $Theme
$config | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'run.json')
$recorder = Join-Path $PSScriptRoot 'Invoke-WinoRecorded.ps1'
$invoke = {
    param([string[]]$Arguments, [string]$ScenarioId)
    & $recorder -RunDirectory $run -ScenarioId $ScenarioId -ArgumentList $Arguments -TimeoutSeconds 1200
}.GetNewClosure()
$activationScript = Join-Path $PSScriptRoot 'Replay-Activation.ps1'
$activate = {
    param([string]$Uri)
    $id = [guid]::NewGuid().ToString('N')
    $event = [ordered]@{ id=$id; state='started'; kind='Uri'; input=$Uri; time=[DateTimeOffset]::Now.ToString('o') }
    $event | ConvertTo-Json -Compress | Add-Content (Join-Path $run 'activations.jsonl')
    $output = & powershell -NoProfile -File $activationScript -Kind Uri -InputValue $Uri
    $code = $LASTEXITCODE
    $output | Set-Content (Join-Path $run "commands/$id.activation.txt")
    $event.state = 'completed'
    $event['exitCode'] = $code
    $event['output'] = "commands/$id.activation.txt"
    $event | ConvertTo-Json -Compress | Add-Content (Join-Path $run 'activations.jsonl')
    if ($code -ne 0) { throw 'Activation helper failed.' }
    ($output -join "`n") | ConvertFrom-Json
}.GetNewClosure()
$launch = @('run',$project,'-c','Debug','-r','win-x64','-p','Platform=x64','-p','GenerateAppxPackageOnBuild=false','-p','AppxPackageSigningEnabled=false','--detach','--json')
if (-not $Restore) { $launch += '--no-restore' }
$relaunch = @($launch | Where-Object { $_ -ne '--no-restore' }) + @('--no-build','--no-restore')
$context = @{
    RunDirectory=$run; Prefix=$config.prefix; Account=$Account; ContactDestination=$ContactDestination
    Scenario='PRE-FLIGHT'; Window=''; ProcessId=0; DebugExecutable=''
    Invoke=$invoke; Activate=$activate; RelaunchArguments=$relaunch
}
Initialize-WinoRegression $context
$results = [System.Collections.Generic.List[object]]::new()
foreach ($name in $Scenario) { $results.Add([pscustomobject]@{ scenario=$name; status='NOT-RUN'; error=$null; durationMs=0 }) }
$failure = $null
$deployed = $false
$started = [DateTimeOffset]::Now

function Get-SourceStamp {
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
    $roots = @('src','controls','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','nuget.config','global.json','WinoMail.slnx','.config')
    $diff = & git diff --binary HEAD -- @roots
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source changes.' }
    $untracked = @(& git ls-files --others --exclude-standard -- @roots)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read untracked source files.' }
    $hashes = foreach ($path in $untracked) { "$path=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)" }
    $bytes = [Text.Encoding]::UTF8.GetBytes((@($head) + @($diff) + @($hashes)) -join "`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

try {
    $version = ((Invoke-RegressionCommand @('--version')) -join '').Trim()
    $readiness = Get-WinoDebugReadiness -ProjectPath $project -WinAppVersion $version
    $readiness | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'deployment-readiness.json')
    if (-not $readiness.Ready) { throw "$($readiness.Code): $($readiness.Reason) $($readiness.NextAction)" }
    $before = Get-SourceStamp
    # Stop only the executable belonging to the verified existing development registration.
    $expected = @($readiness.InstalledPackages | ForEach-Object { Join-Path $_.InstallLocation 'Wino.Mail.WinUI.exe' })
    foreach ($process in @(Get-Process Wino.Mail.WinUI -ErrorAction SilentlyContinue)) {
        if ($process.Path -notin $expected) { throw 'A running Wino process is outside the verified Debug registration.' }
        Stop-Process -Id $process.Id -ErrorAction Stop
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    Invoke-RegressionCommand $launch | Out-Null
    if ((Get-SourceStamp) -ne $before) { throw 'Source changed during deployment. Rebuild before claiming current-source verification.' }
    $after = Get-WinoDebugReadiness -ProjectPath $project -WinAppVersion $version
    if (-not $after.Ready -or $after.InstalledPackages.Count -ne 1) { throw 'Current Debug registration was not established.' }
    $debugRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/Wino.Mail.WinUI/bin/x64/Debug')) + [IO.Path]::DirectorySeparatorChar
    $installedRoot = [IO.Path]::GetFullPath($after.InstalledPackages[0].InstallLocation)
    if (-not ($installedRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($debugRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed package is outside this checkout Debug output.'
    }
    $window = Update-RegressionWindow
    $context.DebugExecutable = Join-Path $installedRoot 'Wino.Mail.WinUI.exe'
    if ((Get-Process -Id $context.ProcessId).Path -ne $context.DebugExecutable) { throw 'Window belongs to another executable.' }
    $deployed = $true
    [pscustomobject]@{ sourceStamp=$before; project=$project; executable=$context.DebugExecutable; window=$window; time=[DateTimeOffset]::Now.ToString('o') } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'deployment.json')
    & (Join-Path $repositoryRoot 'scripts/audit-xaml-automationids.ps1') *> (Join-Path $run 'automation-ids.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Automation-ID audit failed.' }
    foreach ($result in $results) {
        $context.Scenario = $result.scenario
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            switch ($result.scenario) {
                TasksPersistence { Invoke-TasksPersistenceRegression }
                ContactsPersistence { Invoke-ContactsPersistenceRegression }
                ActivationRouting { Invoke-ActivationRoutingRegression }
            }
            $result.status = 'PASS'
        }
        catch {
            $result.status = 'FAIL'
            $result.error = $_.Exception.Message
            throw
        }
        finally {
            $result.durationMs = $timer.ElapsedMilliseconds
            $result | ConvertTo-Json -Compress | Add-Content (Join-Path $run 'results.jsonl')
        }
    }
}
catch {
    $failure = $_.Exception.Message
    if (-not $deployed) {
        foreach ($result in $results) { $result.status='BLOCKED'; $result.error=$failure }
    }
}
finally {
    $records = @(Get-Content (Join-Path $run 'records.json') -Raw | ConvertFrom-Json)
    $leftovers = @($records | Where-Object state -ne removed)
    $commandLog = Join-Path $run 'commands.jsonl'
    $commandCount = if (Test-Path $commandLog) {
        @(Get-Content $commandLog | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object state -eq started).Count
    } else { 0 }
    $summary = [ordered]@{
        started=$started.ToString('o'); finished=[DateTimeOffset]::Now.ToString('o')
        durationMs=[long]([DateTimeOffset]::Now-$started).TotalMilliseconds; winAppCommandCount=$commandCount
        currentSourceDeployed=$deployed; reportedTheme=$Theme; themeVerified=$false
        processId=$context.ProcessId; window=$context.Window; results=@($results); error=$failure
        remainingRecords=$leftovers; evidence='commands.jsonl'
        limits='Only selected scripted scenarios. Persistence means local restart assertions, not independent server verification. Contact deletion is checked immediately. Full baseline and visual theme audit are not claimed.'
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'summary.json')
    @"
# Scripted regression run
Current source deployed: $deployed
Reported theme: $Theme (not independently verified)
Results: summary.json
Commands: commands.jsonl
Remaining synthetic records: $($leftovers.Count)
Error: $failure

After a failure or interruption, inspect records.json and current UI before cleanup or retry.
No automatic mutation retry or failure cleanup was attempted.
"@ | Set-Content (Join-Path $run 'report.md')
    Write-Host "Regression report: $run"
    $results | Format-Table scenario,status,durationMs -AutoSize | Out-Host
    Set-Location $originalLocation
}
if ($failure) { throw $failure }
