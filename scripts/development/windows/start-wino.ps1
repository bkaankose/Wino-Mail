#requires -Version 7.2
<#
.SYNOPSIS
Build changed Debug x64 inputs, then deploy and launch the WinoMail.Debug development package.
.DESCRIPTION
F5 uses this single helper to avoid the SDK's repeated XAML work on unchanged inputs.
State is local to artifacts/f5. Failed runs never become a reusable build.
.PARAMETER ForceBuild
Ignore saved state, for example after changing external build tools or environment settings.
.PARAMETER Target
Deploy locally (the default) or in Windows Sandbox using WinApp CLI 0.7.1 or later. A Sandbox launch always starts
with the host's fixed-version WebView2 runtime and, once per Sandbox, the remote mail lab accounts.
.PARAMETER Reseed
Sandbox only: replace the guest's Wino database with freshly generated lab accounts even if it was seeded before.
.PARAMETER NoAccounts
Sandbox only: start Wino with no accounts, for example to work on the welcome window. The guest's Wino database and
credentials are removed, so the next launch without this switch seeds the lab accounts again.
#>
[CmdletBinding()]
param(
    [switch]$ForceBuild,
    [ValidateSet('local', 'sandbox')][string]$Target = 'local',
    [switch]$Reseed,
    [switch]$NoAccounts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WinoInputFiles([string]$Root) {
    $excluded = @('bin', 'obj', 'artifacts', 'AppPackages', 'BundleArtifacts', '.git', '.vs', 'node_modules')
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($folder in @('src', 'controls', 'icons', 'build', '.config')) {
        $path = Join-Path $Root $folder
        if (Test-Path -LiteralPath $path) { $pending.Push($path) }
    }
    while ($pending.Count) {
        $directory = $pending.Pop()
        [IO.Directory]::GetFiles($directory)
        foreach ($child in [IO.Directory]::GetDirectories($directory)) {
            if ([IO.Path]::GetFileName($child) -notin $excluded) { $pending.Push($child) }
        }
    }
    Get-ChildItem -LiteralPath $Root -File -Force | Where-Object {
        $_.Extension -in @('.props', '.targets', '.json', '.config', '.sln', '.slnx', '.rsp') -or $_.Name -eq '.editorconfig'
    } | ForEach-Object FullName
    Join-Path $Root 'scripts/development/windows/start-wino.ps1'
    Join-Path $Root 'scripts/release/windows/WinoPackageProfile.ps1'
    Get-ChildItem -LiteralPath (Join-Path $Root 'scripts/development/windows/DebugAssets') -File -Recurse -Force | ForEach-Object FullName
    $nugetConfig = Join-Path $env:APPDATA 'NuGet/NuGet.Config'
    if (Test-Path -LiteralPath $nugetConfig) { $nugetConfig }
}

function Get-WinoInputHash([string]$Root, [string]$ToolVersions) {
    $records = [Collections.Generic.List[string]]::new()
    $records.Add("Debug|x64|win-x64|$ToolVersions")
    foreach ($file in (Get-WinoInputFiles $Root | Sort-Object -Unique)) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $records.Add("$([IO.Path]::GetRelativePath($Root, $file))|$hash")
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($records -join "`n"))))
}

function Get-WinoOutputHash([string]$Output) {
    foreach ($required in @('Wino.Mail.WinUI.exe', 'Wino.Mail.WinUI.dll', 'Wino.Mail.WinUI.pdb', 'AppX/AppxManifest.xml', 'AppX/Wino.Mail.WinUI.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Output $required) -PathType Leaf)) { return '' }
    }
    # Check the complete output inventory, including dependencies and the deployed layout.
    # Metadata avoids reading the bundled runtime on every F5.
    $records = Get-ChildItem -LiteralPath $Output -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        "$([IO.Path]::GetRelativePath($Output, $_.FullName))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($records -join "`n"))))
}

function Stop-WinoDebugPackage([string]$ProfilePath, [string]$Target = 'local') {
    $releaseProfile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
    $identity = @{ Name = [string]$releaseProfile.PackageName; Publisher = [string]$releaseProfile.Publisher }
    # This block also runs under Windows PowerShell 5.1 in the guest.
    $stopPackage = {
        param([string]$Name, [string]$Publisher)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $packages = @(Get-AppxPackage -Name $Name)
        foreach ($package in $packages) {
            if ($package.Name -ne $Name -or $package.Publisher -ne $Publisher -or -not $package.IsDevelopmentMode) {
                throw 'The installed package does not match the development identity. Its installation and data were preserved.'
            }
            if ([string]::IsNullOrWhiteSpace($package.InstallLocation)) {
                throw 'The development package install location is missing. No processes were stopped.'
            }
        }
        foreach ($package in $packages) {
            $prefix = $package.InstallLocation.TrimEnd('\', '/') + '\'
            Get-Process | Where-Object {
                $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
            } | Stop-Process -Force
        }
    }
    if ($Target -eq 'sandbox') {
        $nameLiteral = $identity.Name.Replace("'", "''")
        $publisherLiteral = $identity.Publisher.Replace("'", "''")
        $command = "& { $stopPackage } -Name '$nameLiteral' -Publisher '$publisherLiteral'"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        & winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded
        if ($LASTEXITCODE) { throw "Sandbox package preflight failed with exit code $LASTEXITCODE. Deployment was not attempted." }
    } else {
        & $stopPackage -Name $identity.Name -Publisher $identity.Publisher
    }
}

function Get-SandboxWebView2Runtime {
    $root = if ($env:WINO_SANDBOX_WEBVIEW2) { $env:WINO_SANDBOX_WEBVIEW2 } else { Join-Path $HOME '.wino-lab/sandbox/webview2' }
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    if (Test-Path -LiteralPath (Join-Path $root 'msedgewebview2.exe') -PathType Leaf) { return Get-Item -LiteralPath $root }
    Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'msedgewebview2.exe') -PathType Leaf } |
        Sort-Object { [version]($_.Name -replace '^.*?(\d+(\.\d+){3}).*$', '$1') } -Descending |
        Select-Object -First 1
}

function Initialize-SandboxWebView2 {
    # Windows Sandbox has no WebView2 Runtime, and the guest must not download one, so the fixed-version runtime
    # kept on this machine is copied in and named through WEBVIEW2_BROWSER_EXECUTABLE_FOLDER before Wino starts.
    $runtime = Get-SandboxWebView2Runtime
    if (-not $runtime) {
        Write-Warning ('No fixed-version WebView2 runtime found under ~/.wino-lab/sandbox/webview2 (or WINO_SANDBOX_WEBVIEW2). ' +
            'Mail rendering and the composer will not work in Sandbox. See docs/harness/development-windows.md.')
        return
    }
    $version = $runtime.Name -replace '^.*?(\d+(\.\d+){3}).*$', '$1'
    $relative = "webview2/$version"
    Write-Host "Wino F5: copying WebView2 runtime $version into Sandbox."
    & winapp target push sandbox $runtime.FullName $relative -q
    if ($LASTEXITCODE) { throw "Copying the WebView2 runtime into Sandbox failed with exit code $LASTEXITCODE." }

    # The work area is C:\WinApp\work in the guest. This block runs under Windows PowerShell 5.1 there.
    $configure = {
        param([string]$Folder)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        if (-not (Test-Path -LiteralPath (Join-Path $Folder 'msedgewebview2.exe'))) { throw "WebView2 runtime is missing at $Folder." }
        # Packaged apps read a fixed-version runtime only when app packages may read and execute it.
        & icacls.exe $Folder /grant '*S-1-15-2-1:(OI)(CI)(RX)' '*S-1-15-2-2:(OI)(CI)(RX)' /T /Q | Out-Null
        if ($LASTEXITCODE) { throw "icacls failed with exit code $LASTEXITCODE." }
        if ([Environment]::GetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER', 'Machine') -ne $Folder) {
            [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER', $Folder, 'Machine')
        }
    }
    $folderLiteral = "C:\WinApp\work\webview2\$version".Replace("'", "''")
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("& { $configure } -Folder '$folderLiteral'"))
    & winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded
    if ($LASTEXITCODE) { throw "Configuring the WebView2 runtime in Sandbox failed with exit code $LASTEXITCODE." }
}

function Invoke-SandboxScript([scriptblock]$Script, [hashtable]$Arguments = @{}) {
    # Runs under Windows PowerShell 5.1 in the guest; arguments travel as single-quoted literals.
    $literals = foreach ($name in $Arguments.Keys) { "-$name '$(([string]$Arguments[$name]).Replace("'", "''"))'" }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("& { $Script } $($literals -join ' ')"))
    & winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded | Out-Host
    return $LASTEXITCODE
}

function Initialize-SandboxAccounts([string]$Root, [string]$PackageName, [switch]$Reseed) {
    # The guest starts empty, so the lab accounts are generated inside it once per Sandbox. Credentials are DPAPI
    # items bound to the guest user, which is why the generator runs there and not on the host.
    $fixtures = Join-Path $HOME '.wino-lab/fixtures.json'
    if (-not (Test-Path -LiteralPath $fixtures -PathType Leaf)) {
        Write-Warning 'The lab manifest ~/.wino-lab/fixtures.json does not exist, so Sandbox starts without accounts. The owner sets it up with scripts/lab/windows/lab.ps1 manifest.'
        return
    }

    $probe = {
        param([string]$Name)
        $ProgressPreference = 'SilentlyContinue'
        $Family = (Get-AppxPackage -Name $Name | Select-Object -First 1).PackageFamilyName
        if ($Family -and (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "Packages\$Family\LocalState\wino-lab.seeded"))) { exit 3 }
        exit 0
    }
    $seeded = (Invoke-SandboxScript $probe @{ Name = $PackageName }) -eq 3
    if ($seeded -and -not $Reseed) {
        Write-Host 'Wino F5: Sandbox already has the lab accounts; keeping its data (use -Reseed to replace it).'
        return
    }

    Write-Host 'Wino F5: generating the lab accounts inside Sandbox.'
    $generator = Join-Path $Root 'artifacts/sandbox/generator'
    $project = Join-Path $Root 'tools/lab/DatabaseGenerator/DatabaseGenerator.csproj'
    & dotnet publish $project -c Debug -p:Platform=x64 -r win-x64 --self-contained true -o $generator -v:q -nologo
    if ($LASTEXITCODE) { throw "Publishing the lab database generator failed with exit code $LASTEXITCODE." }

    & winapp target push sandbox $generator 'wino-lab/generator' -q
    if ($LASTEXITCODE) { throw "Copying the lab generator into Sandbox failed with exit code $LASTEXITCODE." }
    & winapp target push sandbox $fixtures 'wino-lab/fixtures.json' -q
    if ($LASTEXITCODE) { throw "Copying the lab manifest into Sandbox failed with exit code $LASTEXITCODE." }

    $seed = {
        param([string]$Name)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $Family = (Get-AppxPackage -Name $Name | Select-Object -First 1).PackageFamilyName
        if (-not $Family) { throw "The package $Name is not registered in Sandbox." }
        $work = 'C:\WinApp\work\wino-lab'
        $out = Join-Path $work 'out'
        $fixtures = Join-Path $work 'fixtures.json'
        try {
            if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
            # The generator's summary names the lab password; it never reaches the console.
            & (Join-Path $work 'generator\DatabaseGenerator.exe') $out $fixtures 2>&1 |
                ForEach-Object { "$_" -replace '\(password [^)]*\)', '(password hidden)' }
            if ($LASTEXITCODE) { throw "The lab generator failed with exit code $LASTEXITCODE." }

            $localState = Join-Path $env:LOCALAPPDATA "Packages\$Family\LocalState"
            New-Item -ItemType Directory -Path $localState -Force | Out-Null
            Get-ChildItem -LiteralPath $localState -Force |
                Where-Object { $_.Name -like 'Wino210.db*' -or $_.Name -in @('secure', 'credentials') } |
                Remove-Item -Recurse -Force
            Copy-Item -LiteralPath (Join-Path $out 'Wino210.db') -Destination $localState
            Copy-Item -LiteralPath (Join-Path $out 'secure') -Destination $localState -Recurse
            Set-Content -LiteralPath (Join-Path $localState 'wino-lab.seeded') -Value (Get-Date -Format o)
        } finally {
            # The manifest and the generator's account list carry the lab password.
            Remove-Item -LiteralPath $fixtures, $out -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (Invoke-SandboxScript $seed @{ Name = $PackageName }) { throw 'Seeding the lab accounts in Sandbox failed. Wino was not launched.' }
}

function Clear-SandboxAccounts([string]$PackageName) {
    Write-Host 'Wino F5: starting Sandbox without accounts; removing the guest Wino database and credentials.'
    $clear = {
        param([string]$Name)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $Family = (Get-AppxPackage -Name $Name | Select-Object -First 1).PackageFamilyName
        if (-not $Family) { throw "The package $Name is not registered in Sandbox." }
        $localState = Join-Path $env:LOCALAPPDATA "Packages\$Family\LocalState"
        if (-not (Test-Path -LiteralPath $localState)) { return }
        Get-ChildItem -LiteralPath $localState -Force |
            Where-Object { $_.Name -like 'Wino210.db*' -or $_.Name -in @('secure', 'credentials', 'wino-lab.seeded') } |
            Remove-Item -Recurse -Force
    }
    if (Invoke-SandboxScript $clear @{ Name = $PackageName }) { throw 'Removing the guest Wino data failed. Wino was not launched.' }
}

function Start-SandboxWino([string]$PackageName) {
    $launch = {
        param([string]$Name)
        $ProgressPreference = 'SilentlyContinue'
        $Family = (Get-AppxPackage -Name $Name | Select-Object -First 1).PackageFamilyName
        if (-not $Family) { exit 1 }
        Start-Process explorer.exe "shell:AppsFolder\$Family!App"
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $process = Get-Process -Name Wino.Mail.WinUI -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($process) { "Wino F5: Wino is running in Sandbox (process $($process.Id); winapp ui ... --on sandbox -a $($process.Id))."; exit 0 }
            Start-Sleep -Seconds 1
        }
        exit 1
    }
    if (Invoke-SandboxScript $launch @{ Name = $PackageName }) { throw 'Wino did not start in Sandbox within 60 seconds.' }
}

function Start-Wino([string]$Root, [switch]$ForceBuild, [ValidateSet('local', 'sandbox')][string]$Target = 'local', [switch]$Reseed, [switch]$NoAccounts) {
    if ($Reseed -and $NoAccounts) { throw '-Reseed and -NoAccounts cannot be combined.' }
    if (($Reseed -or $NoAccounts) -and $Target -ne 'sandbox') { throw '-Reseed and -NoAccounts apply only to -Target sandbox.' }
    $app = Join-Path $Root 'src/Wino.Mail.WinUI'
    $project = Join-Path $app 'Wino.Mail.WinUI.csproj'
    $framework = ([xml](Get-Content -LiteralPath $project -Raw)).SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText
    $output = Join-Path $app "bin/x64/Debug/$framework/win-x64"
    $stateDirectory = Join-Path $Root 'artifacts/f5'
    $null = New-Item -ItemType Directory -Path $stateDirectory -Force
    $statePath = Join-Path $stateDirectory 'last-success.json'
    # Reject concurrent launches rather than letting two runs overwrite each other's state.
    $lock = [IO.File]::Open((Join-Path $stateDirectory 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Push-Location $Root
    try {
        $sdk = & dotnet --version
        if ($LASTEXITCODE) { throw 'Cannot determine the .NET SDK version.' }
        $winappVersion = & winapp --version
        if ($LASTEXITCODE) { throw 'Cannot determine the WinApp CLI version.' }
        $versions = "$sdk|$winappVersion"
        $inputs = Get-WinoInputHash $Root $versions
        $outputs = Get-WinoOutputHash $output
        $saved = $null
        if (Test-Path -LiteralPath $statePath) {
            try { $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable } catch { }
        }
        $reuse = -not $ForceBuild -and $saved -is [Collections.IDictionary] -and
            $saved['Inputs'] -eq $inputs -and $outputs -ne '' -and $saved['Outputs'] -eq $outputs

        $profilePath = Join-Path $app 'release-profile.Debug.json'
        $packageName = [string](Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json).PackageName
        # The host app and its tray process run from the build output that Sandbox deployment also rewrites.
        Stop-WinoDebugPackage -ProfilePath $profilePath -Target local
        if ($Target -eq 'sandbox') {
            Stop-WinoDebugPackage -ProfilePath $profilePath -Target sandbox
            Initialize-SandboxWebView2
        }
        # Invalidate before starting: build or deployment failure must force the next run to build.
        [IO.File]::WriteAllText($statePath, '{}')
        # Sandbox registers without launching, so WebView2 and the accounts are in place before Wino first starts.
        $runArgs = @('run', $project, '-c', 'Debug', '--arch', 'x64')
        $runArgs += if ($Target -eq 'sandbox') { @('--on', 'sandbox', '--no-launch') } else { @('--detach') }
        if ($reuse) {
            Write-Host 'Wino F5: inputs and output are unchanged; deploying with --no-build.'
            $runArgs += '--no-build'
        } else {
            Write-Host 'Wino F5: inputs, tools, or output changed (or no saved build); building before deployment.'
        }
        & winapp @runArgs
        if ($LASTEXITCODE) { throw "WinApp failed with exit code $LASTEXITCODE. Build state was not saved." }
        if ($Target -eq 'sandbox') {
            if ($NoAccounts) { Clear-SandboxAccounts -PackageName $packageName }
            else { Initialize-SandboxAccounts -Root $Root -PackageName $packageName -Reseed:$Reseed }
            Start-SandboxWino -PackageName $packageName
        }

        $finalOutputs = Get-WinoOutputHash $output
        if ($finalOutputs -eq '') { throw 'WinApp completed, but required Debug output is missing. Build state was not saved.' }
        if ((Get-WinoInputHash $Root $versions) -ne $inputs) {
            Write-Warning 'Inputs changed during this run. The next F5 will build again.'
            return
        }
        @{ Inputs = $inputs; Outputs = $finalOutputs } | ConvertTo-Json | Set-Content -LiteralPath $statePath
    } finally {
        Pop-Location
        $lock.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $IsWindows) { throw 'The Wino F5 helper deploys the Windows MSIX package and requires Windows.' }
    Start-Wino -Root ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))) -ForceBuild:$ForceBuild -Target $Target -Reseed:$Reseed -NoAccounts:$NoAccounts
}
