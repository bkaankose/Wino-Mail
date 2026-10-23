<#
.SYNOPSIS
Starts, seeds, and verifies the five-server local Docker mail lab, then generates Mail-Cal account artifacts.
.EXAMPLE
.\scripts\lab\windows\local-lab.ps1 up
.EXAMPLE
.\scripts\lab\windows\local-lab.ps1 install-app
.EXAMPLE
.\scripts\lab\windows\local-lab.ps1 reset -Force
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'status', 'provision', 'seed', 'verify', 'generate-db', 'install-app', 'stop', 'reset', 'logs', 'help')]
    [string]$Command = 'help',
    [ValidateSet('dovecot', 'condstore', 'stalwart', 'greenmail', 'james', 'baikal', 'radicale-condstore', 'radicale-greenmail', 'radicale-james')]
    [string]$Service,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../tools/local-lab'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$composeFile = Join-Path $labRoot 'compose.yaml'
$stateRoot = Join-Path $labRoot '.state'
$generatedRoot = Join-Path $labRoot 'generated'
$servers = @('dovecot', 'condstore', 'stalwart', 'greenmail', 'james', 'baikal', 'radicale-condstore', 'radicale-greenmail', 'radicale-james')
$publishedPorts = @(1143, 1587, 4025, 2143, 2587, 3143, 3587, 8803, 4143, 4080, 5143, 5587, 5800, 8800, 8802, 8804, 8805)
$packageName = 'hairokuma.MailCal'

function Resolve-Docker {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return }
    foreach ($candidate in @((Join-Path $env:LOCALAPPDATA 'Programs/DockerDesktop/resources/bin'), 'C:\Program Files\Docker\Docker\resources\bin')) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'docker.exe')) { $env:Path = "$env:Path;$candidate"; return }
    }
}

function Invoke-Compose {
    param([string[]]$Arguments)
    & docker compose --project-name mailcal-local-lab --file $composeFile @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed ($LASTEXITCODE). Check Docker Desktop and run local-lab.ps1 logs." }
}

function Assert-Docker {
    Resolve-Docker
    if (!(Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Install/start Docker Desktop with Linux containers, then retry.' }
    $mode = & docker info --format '{{.OSType}}' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Docker engine is unavailable. Start Docker Desktop and retry.' }
    if ($mode -ne 'linux') { throw 'Switch Docker Desktop to Linux containers and retry.' }
    & docker compose version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose v2 is required. Update Docker Desktop.' }
}

function Assert-Ports {
    # Accept an occupied endpoint only if this project's running container owns it.
    $owned = @(& docker compose --project-name mailcal-local-lab --file $composeFile ps --format json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing lab containers.' }
    $running = @()
    foreach ($line in $owned) {
        if ($line) { $running += $line | ConvertFrom-Json }
    }
    foreach ($port in $publishedPorts) {
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
    # Mailboxes and user patches for both docker-mailserver instances, then the Baikal user.
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--entrypoint', 'bash', 'dovecot', '/lab/mail.sh')
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--entrypoint', 'bash', 'condstore', '/lab/mail.sh')
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--entrypoint', 'php', 'baikal', '/lab/dav.php')
}

function Invoke-Seed {
    param([string]$SeedCommand)
    Invoke-Compose -Arguments @('run', '--rm', '--no-deps', 'seed', $SeedCommand)
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
    Invoke-Compose -Arguments (@('up', '-d', '--wait', '--wait-timeout', '300') + $servers)
    Invoke-Seed -SeedCommand 'all'
    Generate-Database
}

function Install-App {
    # The only command that touches application data. It replaces the Debug package's database with the
    # generated one (after backing the old one up), copies the artifacts to the Desktop, and launches Mail-Cal.
    if (!(Test-Path -LiteralPath (Join-Path $generatedRoot 'MailCal210.db'))) { throw 'Generated artifacts are missing. Run local-lab.ps1 up first.' }
    if (!(Get-Command winapp -ErrorAction SilentlyContinue)) { throw 'WinApp CLI 0.7+ must be on PATH for install-app.' }
    $startMailCal = Join-Path $repositoryRoot 'scripts/development/windows/start-mailcal.ps1'
    $manifest = Join-Path $repositoryRoot 'src/MailCal.Mail.WinUI/Package.appxmanifest'
    $project = Join-Path $repositoryRoot 'src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj'
    . $startMailCal
    Write-Host 'Building, registering, and launching the Debug package (reuses unchanged output).'
    Start-MailCal -Root $repositoryRoot
    $package = Get-AppxPackage -Name $packageName
    if (!$package) { throw 'The development package is not registered after launch; cannot locate its LocalState.' }
    $localState = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState"
    Start-Sleep -Seconds 8
    Stop-MailCalDebugPackage -ManifestPath $manifest
    Start-Sleep -Seconds 2
    New-Item -ItemType Directory -Path $localState -Force | Out-Null

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupRoot = Join-Path $labRoot "app-backups/$timestamp"
    $existing = @(Get-ChildItem -LiteralPath $localState -Force | Where-Object { $_.Name -like 'MailCal2*.db*' -or $_.Name -eq 'credentials' })
    if ($existing.Count) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        foreach ($item in $existing) { Move-Item -LiteralPath $item.FullName -Destination $backupRoot -Force }
        Write-Host "Previous application data moved to $backupRoot"
    }
    Copy-Item -LiteralPath (Join-Path $generatedRoot 'MailCal210.db') -Destination $localState -Force
    $credentialTarget = Join-Path $localState 'credentials/dav'
    New-Item -ItemType Directory -Path $credentialTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $generatedRoot 'credentials/dav/*') -Destination $credentialTarget -Force

    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'mailcal-lab'
    New-Item -ItemType Directory -Path $desktop -Force | Out-Null
    foreach ($name in @('MailCal210.db', 'mailcal-lab-backup.mailcalsnap', 'manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $generatedRoot $name) -Destination $desktop -Force
    }
    Write-Host "Database and encrypted backup (password 1) copied to $desktop"

    & winapp run $project --arch x64 --no-build --detach
    if ($LASTEXITCODE) { throw "WinApp failed to launch the package ($LASTEXITCODE)." }
    Write-Host 'Mail-Cal launched with the five lab accounts. All passwords are 1.'
}

$operationLock = $null
try {
    if ($Command -eq 'help') {
        @'
Mail-Cal local lab (Windows PowerShell 5.1+ / PowerShell 7)
Prerequisites: Docker Desktop (Linux containers, Compose v2), repository .NET SDK, WinApp CLI for install-app.
Run from any directory: scripts/lab/windows/local-lab.ps1 <command>

up             Start all servers, provision accounts, seed ~150 messages + calendars + contacts per account,
               verify capabilities and cross-server delivery, generate client artifacts.
status         Container state, endpoint reachability, artifact paths.
provision      Re-run account provisioning on Stalwart and James.
seed           Add missing fixtures to running servers; preserve existing data/edits.
verify         Print capabilities per server and send delivery probes between every account pair.
generate-db    Generate MailCal210.db, DAV credentials, and mailcal-lab-backup.mailcalsnap (password 1).
install-app    Build/register the Debug package, replace its database with the generated one (old data is
               backed up), copy artifacts to Desktop\mailcal-lab, and launch Mail-Cal.
stop           Stop services, retain data.
reset -Force   Delete ONLY this lab's server volumes and seed state, rebuild everything.
logs           Recent server logs. Optional: -Service <name>
help           This help.

Servers (IMAP / SMTP / DAV on 127.0.0.1), password 1 everywhere:
  dovecot    alice@dovecot.test     1143 / 1587 / http://127.0.0.1:8800/dav.php/   QRESYNC
  condstore  bob@condstore.test     2143 / 2587 / http://127.0.0.1:8802/           CONDSTORE only
  stalwart   carol@stalwart.test    3143 / 3587 / http://127.0.0.1:8803/dav/       QRESYNC (IMAP4rev2)
  greenmail  dave@greenmail.test    4143 / 4025 (hub, no auth) / http://127.0.0.1:8804/  UID only
  james      eve@james.test         5143 / 5587 / http://127.0.0.1:8805/           probed
Output: tools/local-lab/generated (MailCal210.db, credentials/dav, mailcal-lab-backup.mailcalsnap, manifest.json).
See tools/local-lab/README.md for details.
'@ | Write-Output
        return
    }
    if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) { throw 'The local lab currently runs on Windows only.' }
    if ($Command -notin @('status', 'logs')) {
        try {
            $operationLock = [IO.File]::Open((Join-Path $labRoot '.operation.lock'),
                [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch { throw 'Another local-lab command is running. Wait for it to finish before retrying.' }
    }
    if ($Command -eq 'generate-db') { Generate-Database; return }
    if ($Command -eq 'install-app') { Install-App; return }
    Assert-Docker
    switch ($Command) {
        'up' { Start-Lab }
        'provision' { Invoke-Seed -SeedCommand 'provision' }
        'seed' { Invoke-Seed -SeedCommand 'seed' }
        'verify' { Invoke-Seed -SeedCommand 'verify' }
        'stop' { Invoke-Compose -Arguments (@('stop') + $servers) }
        'logs' {
            $targets = $servers
            if ($Service) { $targets = @($Service) }
            Invoke-Compose -Arguments (@('logs', '--tail', '100') + $targets)
        }
        'status' {
            Invoke-Compose -Arguments @('ps', '--all')
            foreach ($port in $publishedPorts) {
                $client = [Net.Sockets.TcpClient]::new()
                try {
                    $task = $client.ConnectAsync('127.0.0.1', $port)
                    $reachable = $task.Wait(1000) -and $client.Connected
                } catch { $reachable = $false }
                finally { $client.Dispose() }
                Write-Output "127.0.0.1:$port reachable=$reachable"
            }
            Write-Output "Artifacts: $generatedRoot (exists=$(Test-Path -LiteralPath (Join-Path $generatedRoot 'MailCal210.db')))"
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
