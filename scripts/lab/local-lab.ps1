<#
.SYNOPSIS
Starts/seeds the local Docker servers and generates fresh Wino account artifacts.
.EXAMPLE
.\scripts\lab\local-lab.ps1 up
.EXAMPLE
.\scripts\lab\local-lab.ps1 reset -Force
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'status', 'seed', 'generate-db', 'stop', 'reset', 'logs', 'help')]
    [string]$Command = 'help',
    [ValidateSet('mail', 'dav')][string]$Service,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/local-lab'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$composeFile = Join-Path $labRoot 'compose.yaml'
$stateRoot = Join-Path $labRoot '.state'
$generatedRoot = Join-Path $labRoot 'generated'

function Invoke-Compose {
    param([string[]]$Arguments)
    & docker compose --project-name wino-local-lab --file $composeFile @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed ($LASTEXITCODE). Check Docker Desktop and run local-lab.ps1 logs." }
}

function Assert-Docker {
    if (!(Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Install/start Docker Desktop with Linux containers, then retry.' }
    $mode = & docker info --format '{{.OSType}}' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Docker engine is unavailable. Start Docker Desktop and retry.' }
    if ($mode -ne 'linux') { throw 'Switch Docker Desktop to Linux containers and retry.' }
    & docker compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose v2 is required. Update Docker Desktop.' }
}

function Assert-Ports {
    # Accept an occupied endpoint only if this project's running container owns it.
    $owned = @(& docker compose --project-name wino-local-lab --file $composeFile ps --format json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing lab containers.' }
    $running = @()
    foreach ($line in $owned) {
        if ($line) { $running += $line | ConvertFrom-Json }
    }
    foreach ($port in @(1143, 1587, 8800)) {
        if (@($running | Where-Object State -eq 'running' | ForEach-Object Publishers | Where-Object PublishedPort -eq $port).Count) { continue }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        try { $listener.Start() }
        catch { throw "Local port $port is occupied by another service. Free it before starting the lab." }
        finally { $listener.Stop() }
    }
}

function Assert-Sdk {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET SDK selected by global.json, then retry.' }
    Push-Location $repositoryRoot
    try {
        & dotnet --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Install the .NET SDK selected by the repository global.json, then retry.' }
    } finally { Pop-Location }
}

function Initialize-Lab {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--entrypoint', 'bash', 'mail', '/lab/mail.sh')
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--entrypoint', 'php', 'dav', '/lab/dav.php')
    # The normal Baikal entry point fixes ownership of initialized volume files.
}

function Seed-Lab {
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', 'seed', 'seed')
}

function Generate-Database {
    Assert-Sdk
    $catalog = Join-Path $stateRoot 'fixtures.json'
    if (!(Test-Path -LiteralPath $catalog)) { throw 'Fixture manifest is missing. Run local-lab.ps1 up first.' }
    Push-Location $repositoryRoot
    try {
        & dotnet run --project (Join-Path $labRoot 'DatabaseGenerator/DatabaseGenerator.csproj') --configuration Debug --property:Platform=x64 -- $generatedRoot $catalog
        if ($LASTEXITCODE -ne 0) { throw 'Database generation failed. Existing generated artifacts were preserved. Check the .NET SDK/build output above.' }
    } finally { Pop-Location }
}

function Start-Lab {
    Assert-Sdk
    Assert-Ports
    Initialize-Lab
    Invoke-Compose -Arguments @('up', '-d', 'mail', 'dav')
    Seed-Lab
    Generate-Database
}

$operationLock = $null
try {
    if ($Command -eq 'help') {
        @'
Wino local lab (Windows PowerShell 5.1+ / PowerShell 7)
Prerequisites: Docker Desktop (Linux containers, Compose v2), repository .NET SDK.
Run from any directory: scripts/lab/local-lab.ps1 <command>

up             Start services, provision/seed missing fixtures, generate client artifacts.
status         Container state, endpoint reachability, artifact paths.
seed           Add missing fixtures to running services; preserve existing data/edits.
generate-db    Generate fresh Wino210.db and current-user DAV credentials.
stop           Stop services, retain data.
reset -Force   Delete ONLY this lab's server volumes and seed state, rebuild baseline.
logs           Recent server logs. Optional: -Service mail | dav
help           This help.

Endpoints: IMAP 127.0.0.1:1143, SMTP 127.0.0.1:1587, DAV http://127.0.0.1:8800/dav.php/
Mail users: alice@wino.test, bob@wino.test, empty@wino.test. DAV users: alice, bob, empty.
Local fixture password: WinoLab123!
Output: tools/local-lab/generated (database, credentials/dav, manifest.json).
Copy artifacts manually into a fresh Wino LocalState directory while Wino is closed.
No installed app data is changed. See tools/local-lab/README.md for the fixture catalog.
'@ | Write-Output
        return
    }
    if ($Command -notin @('status', 'logs')) {
        try {
            $operationLock = [IO.File]::Open((Join-Path $labRoot '.operation.lock'),
                [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch { throw 'Another local-lab command is running. Wait for it to finish before retrying.' }
    }
    if ($Command -eq 'generate-db') { Generate-Database; return }
    Assert-Docker
    switch ($Command) {
        'up' { Start-Lab }
        'seed' { Seed-Lab }
        'stop' { Invoke-Compose -Arguments @('stop', 'mail', 'dav') }
        'logs' {
            $services = @('mail', 'dav')
            if ($Service) { $services = @($Service) }
            Invoke-Compose -Arguments (@('logs', '--tail', '100') + $services)
        }
        'status' {
            Invoke-Compose -Arguments @('ps', '--all')
            foreach ($port in @(1143, 1587, 8800)) {
                $client = [Net.Sockets.TcpClient]::new()
                try {
                    $task = $client.ConnectAsync('127.0.0.1', $port)
                    $reachable = $task.Wait(1000) -and $client.Connected
                } catch { $reachable = $false }
                finally { $client.Dispose() }
                Write-Output "127.0.0.1:$port reachable=$reachable"
            }
            Write-Output "Artifacts: $generatedRoot (exists=$(Test-Path -LiteralPath (Join-Path $generatedRoot 'Wino210.db')))"
        }
        'reset' {
            if (!$Force) { throw 'Reset deletes this lab baseline. Use reset -Force to explicitly request it.' }
            Invoke-Compose -Arguments @('down', '--volumes', '--remove-orphans')
            $resolvedState = [IO.Path]::GetFullPath($stateRoot)
            if ($resolvedState -ne (Join-Path $labRoot '.state')) { throw 'Unexpected state path; refusing reset.' }
            if (Test-Path -LiteralPath $resolvedState) { Remove-Item -LiteralPath $resolvedState -Recurse -Force }
            Start-Lab
        }
    }
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($null -ne $operationLock) { $operationLock.Dispose() }
}
