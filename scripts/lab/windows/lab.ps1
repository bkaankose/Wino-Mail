<#
.SYNOPSIS
Connects this machine to the remote Wino mail lab: checks its endpoints, fetches the account manifest,
installs the lab accounts into the Debug app, and opens the tunnel the synchronization lab tool uses.
.EXAMPLE
.\scripts\lab\windows\lab.ps1 status
.EXAMPLE
.\scripts\lab\windows\lab.ps1 install-app
.EXAMPLE
.\scripts\lab\windows\lab.ps1 tunnel
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'manifest', 'generate-db', 'install-app', 'tunnel', 'help')]
    [string]$Command = 'help'
)
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../tools/lab'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$stateRoot = if ($env:WINO_LAB_STATE) { $env:WINO_LAB_STATE } else { Join-Path $HOME ".wino-lab" }
$manifestPath = Join-Path $stateRoot 'fixtures.json'
$generatedRoot = Join-Path $stateRoot "generated"
$labHost = if ($env:WINO_LAB_HOST) { $env:WINO_LAB_HOST } else { 'lab.winomail.app' }
$sshHost = if ($env:WINO_LAB_SSH_HOST) { $env:WINO_LAB_SSH_HOST } else { 'wino-lab' }
$servers = @('dovecot', 'condstore', 'stalwart', 'greenmail', 'james')
# The lab's plaintext loopback ports, forwarded one to one by `tunnel`.
$tunnelPorts = @(1143, 1587, 4025, 2143, 2587, 3143, 3587, 8803, 4143, 5143, 5587, 8800, 8802, 8804, 8805,
    1993, 2993, 3993, 4993, 1465, 2465, 3465, 4465, 8443, 8444, 8445, 8446, 8807)

function Test-Endpoint {
    param([string]$HostName, [int]$Port)
    $client = [Net.Sockets.TcpClient]::new()
    try { return $client.ConnectAsync($HostName, $Port).Wait(3000) -and $client.Connected }
    catch { return $false }
    finally { $client.Dispose() }
}

function Show-Status {
    foreach ($server in $servers) {
        $name = "$server.$labHost"
        $ports = if ($server -eq 'greenmail') { @(993, 443) } else { @(993, 465, 443) }
        foreach ($port in $ports) { Write-Output "${name}:$port reachable=$(Test-Endpoint -HostName $name -Port $port)" }
    }
    Write-Output "Manifest: $manifestPath (exists=$(Test-Path -LiteralPath $manifestPath))"
    Write-Output "Generated database: $generatedRoot (exists=$(Test-Path -LiteralPath (Join-Path $generatedRoot 'Wino210.db')))"
}

function Get-Manifest {
    # The manifest carries the lab password, so it is kept outside the repository and is never printed.
    if (!(Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'OpenSSH client (ssh) is required to fetch the manifest.' }
    $json = & ssh $sshHost 'wino-lab/lab.sh manifest'
    if ($LASTEXITCODE -ne 0 -or !$json) { throw "Could not read the manifest from '$sshHost'. Check the SSH alias (WINO_LAB_SSH_HOST) and that the lab is up." }
    $parsed = ($json -join "`n") | ConvertFrom-Json
    if (!$parsed.accounts -or !$parsed.password) { throw 'The lab returned a manifest without accounts or password.' }
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    Set-Content -LiteralPath $manifestPath -Value ($json -join "`n") -Encoding utf8NoBOM
    Write-Output "Manifest saved to $manifestPath ($($parsed.accounts.Count) accounts)."
}

function New-Database {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET SDK selected by global.json and add dotnet to PATH.' }
    if (!(Test-Path -LiteralPath $manifestPath)) { Get-Manifest }
    Push-Location $repositoryRoot
    try {
        # The generator's summary line names the password; keep it out of the console.
        & dotnet run --project (Join-Path $labRoot 'DatabaseGenerator/DatabaseGenerator.csproj') --configuration Debug --property:Platform=x64 -- $generatedRoot $manifestPath |
            ForEach-Object { $_ -replace '\(password [^)]*\)', '(password hidden)' }
        if ($LASTEXITCODE -ne 0) { throw 'Database generation failed. Existing generated artifacts were preserved. Check the build output above.' }
    } finally { Pop-Location }
}

function Install-App {
    # The only command that touches application data: it replaces the registered Debug package's database with
    # the generated one after moving the old one to app-backups.
    $package = @(Get-AppxPackage | Where-Object { $_.IsDevelopmentMode -and $_.Name -match 'WinoMail' }) | Select-Object -First 1
    if (!$package) { throw 'No Wino development package is registered. Start the Debug app once (F5), close it, and retry.' }
    if (Get-Process -Name 'Wino.Mail.WinUI' -ErrorAction SilentlyContinue) { throw 'Wino is running. Close it and retry; its data is not replaced while it runs.' }
    New-Database
    $localState = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState"
    New-Item -ItemType Directory -Path $localState -Force | Out-Null

    $backupRoot = Join-Path $stateRoot "app-backups/$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $existing = @(Get-ChildItem -LiteralPath $localState -Force | Where-Object { $_.Name -like 'Wino2*.db*' -or $_.Name -eq 'credentials' })
    if ($existing.Count) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        foreach ($item in $existing) { Move-Item -LiteralPath $item.FullName -Destination $backupRoot -Force }
        Write-Output "Previous application data moved to $backupRoot"
    }
    Copy-Item -LiteralPath (Join-Path $generatedRoot 'Wino210.db') -Destination $localState -Force
    $credentialTarget = Join-Path $localState 'credentials/dav'
    New-Item -ItemType Directory -Path $credentialTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $generatedRoot 'credentials/dav/*') -Destination $credentialTarget -Force

    Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!App"
    Write-Output "Wino ($($package.Name)) launched with the lab accounts."
}

function Start-Tunnel {
    if (!(Get-Command ssh -ErrorAction SilentlyContinue)) { throw 'OpenSSH client (ssh) is required for the tunnel.' }
    $busy = @($tunnelPorts | Where-Object { Test-Endpoint -HostName '127.0.0.1' -Port $_ })
    if ($busy.Count) { throw "Local ports already in use: $($busy -join ', '). Close the other tunnel or local lab first." }
    $forwards = foreach ($port in $tunnelPorts) { '-L'; "127.0.0.1:${port}:127.0.0.1:$port" }
    Write-Output "Forwarding the lab's loopback ports from '$sshHost'. Press Ctrl+C to close the tunnel."
    & ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 @forwards $sshHost
}

function Show-Help {
    @"
Wino mail lab client (PowerShell 7+)
The lab itself runs on a remote Linux host and is not part of this repository. This script only connects
this machine to it. See tools/lab/README.md.

.\scripts\lab\windows\lab.ps1 <command>

status         Reachability of the lab's public endpoints and the state of local artifacts.
manifest       Fetch the account manifest (accounts, endpoints, password) from the lab host over SSH
               into ~/.wino-lab/fixtures.json, outside the repository.
generate-db    Generate Wino210.db and DAV credentials for the lab accounts from the manifest.
install-app    Replace the registered Debug package's database with the generated one (the old data is
               moved to ~/.wino-lab/app-backups) and launch Wino. Wino must be closed.
tunnel         Forward the lab's plaintext loopback ports to 127.0.0.1 (unauthenticated hub port,
               certificate test endpoints).
help           This help.

Settings (environment):
  WINO_LAB_HOST      public DNS suffix of the lab (default lab.winomail.app)
  WINO_LAB_SSH_HOST  SSH host or alias of the lab machine (default wino-lab); needed by manifest and tunnel
  WINO_LAB_STATE     folder for the manifest and generated data (default ~/.wino-lab)

Public endpoints, TLS: IMAP 993, SMTP 465, CalDAV/CardDAV 443 on <server>.$labHost
  dovecot (QRESYNC), condstore (CONDSTORE only), stalwart (IMAP4rev2), greenmail (UID only, IMAP and DAV only), james
"@
}

try {
    switch ($Command) {
        'help' { Show-Help }
        'status' { Show-Status }
        'manifest' { Get-Manifest }
        'generate-db' { New-Database }
        'install-app' { Install-App }
        'tunnel' { Start-Tunnel }
    }
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
