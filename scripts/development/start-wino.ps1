#requires -Version 7.2
<#
.SYNOPSIS
Build changed Debug x64 inputs, then deploy and launch the existing MSIX identity.
.DESCRIPTION
F5 uses this single helper to avoid the SDK's repeated XAML work on unchanged inputs.
State is local to artifacts/f5. Failed runs never become a reusable build.
.PARAMETER ForceBuild
Ignore saved state, for example after changing external build tools or environment settings.
.PARAMETER Target
Deploy locally (the default) or in Windows Sandbox using WinApp CLI 0.7.1 or later.
#>
[CmdletBinding()]
param(
    [switch]$ForceBuild,
    [ValidateSet('local', 'sandbox')][string]$Target = 'local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WinoInputFiles([string]$Root) {
    $excluded = @('bin', 'obj', 'artifacts', 'AppPackages', 'BundleArtifacts', '.git', '.vs', 'node_modules')
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($folder in @('src', 'controls', 'icons', '.config')) {
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
    Join-Path $Root 'scripts/development/start-wino.ps1'
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

function Stop-WinoDebugPackage([string]$ManifestPath, [string]$Target = 'local') {
    $identity = ([xml](Get-Content -LiteralPath $ManifestPath -Raw)).Package.Identity
    # This block also runs under Windows PowerShell 5.1 in the guest.
    $stopPackage = {
        param([string]$Name, [string]$Publisher)
        $ErrorActionPreference = 'Stop'
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

function Start-Wino([string]$Root, [switch]$ForceBuild, [ValidateSet('local', 'sandbox')][string]$Target = 'local') {
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

        Stop-WinoDebugPackage -ManifestPath (Join-Path $app 'Package.appxmanifest') -Target $Target
        # Invalidate before starting: build or deployment failure must force the next run to build.
        [IO.File]::WriteAllText($statePath, '{}')
        $runArgs = @('run', $project, '-c', 'Debug', '--arch', 'x64', '--detach')
        if ($Target -eq 'sandbox') { $runArgs += @('--on', 'sandbox', '--json') }
        if ($reuse) {
            Write-Host 'Wino F5: inputs and output are unchanged; deploying with --no-build.'
            $runArgs += '--no-build'
        } else {
            Write-Host 'Wino F5: inputs, tools, or output changed (or no saved build); building before deployment.'
        }
        & winapp @runArgs
        if ($LASTEXITCODE) { throw "WinApp failed with exit code $LASTEXITCODE. Build state was not saved." }

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
    Start-Wino -Root ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) -ForceBuild:$ForceBuild -Target $Target
}
