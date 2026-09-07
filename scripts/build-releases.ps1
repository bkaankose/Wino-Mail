#requires -Version 7.0
<#
.SYNOPSIS
Builds Store, Beta, and stable sideload packages from one Release compilation.
.DESCRIPTION
Asks for channels and architectures. Reads the source manifest version. Outputs
verified packages under src/Wino.Mail.WinUI/AppPackages. See docs/releases.md.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ReleaseRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:SideloadIdentityName = 'WinoMail.Sideload'
$script:SideloadPublisher = 'CN=Burak Kaan Köse, O=Burak Kaan Köse, L=Wroclaw, S=Dolnośląskie, C=PL'

function Read-ReleaseChoice {
    param([string]$Prompt, [hashtable]$Choices)

    while ($true) {
        $answer = Read-Host $Prompt
        if ($null -eq $answer) { throw 'Input ended before all release selections were supplied.' }
        $answer = $answer.Trim().ToLowerInvariant()
        if ($Choices.ContainsKey($answer)) { return $Choices[$answer] }
        Write-Host 'Select one of the displayed options.' -ForegroundColor Yellow
    }
}

function Read-ReleaseSelection {
    $yesNo = @{ yes = $true; y = $true; no = $false; n = $false }
    $store = Read-ReleaseChoice 'Store release? (yes/no)' $yesNo
    $beta = Read-ReleaseChoice 'Beta release? (yes/no)' $yesNo
    $sideload = Read-ReleaseChoice 'Stable sideload release? (yes/no)' $yesNo
    if (-not $store -and -not $beta -and -not $sideload) { return $null }

    $architectures = Read-ReleaseChoice 'Architectures? (1 = x64 only, 2 = All)' @{
        '1' = @('x64'); 'x64' = @('x64'); 'x64 only' = @('x64')
        '2' = @('x86', 'x64', 'ARM64'); 'all' = @('x86', 'x64', 'ARM64')
    }
    return [pscustomobject]@{ Store = $store; Beta = $beta; Sideload = $sideload; Architectures = @($architectures) }
}

function Get-ReleaseVersion {
    param([string]$Text)

    if ($Text -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.0$') {
        throw 'The manifest version must have four numeric components and a zero revision, such as 2.0.55.0.'
    }
    $parts = $Text.Split('.')
    foreach ($part in $parts) {
        $number = 0
        if (-not [int]::TryParse($part, [ref]$number) -or $number -gt 65535) {
            throw 'Each manifest version component must be between 0 and 65535.'
        }
    }
    if ([int]$parts[0] -eq 0) { throw 'The manifest major version must be greater than zero.' }
    return $Text
}

function Resolve-ReleaseChildPath {
    param([string]$Root, [string]$RelativePath)

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if ([IO.Path]::IsPathRooted($RelativePath) -or
        -not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The release path must stay within its staging directory: $RelativePath"
    }
    return $fullPath
}

function Assert-ReleaseDestinations {
    param([string[]]$Destinations)

    foreach ($destination in $Destinations) {
        if (Test-Path -LiteralPath $destination) {
            throw "The release destination already exists. Move it or change the manifest version: $destination"
        }
    }
}

function New-ReleasePlan {
    param([object]$Selection, [string]$RepositoryRoot = $script:ReleaseRepositoryRoot)

    $project = Join-Path $RepositoryRoot 'src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj'
    $manifestPath = Join-Path (Split-Path $project) 'Package.appxmanifest'
    $manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    $version = Get-ReleaseVersion ([string]$manifest.Package.Identity.Version)
    $outputRoot = Join-Path (Split-Path $project) 'AppPackages'
    $sideloadChannels = @()
    if ($Selection.Beta) { $sideloadChannels += [pscustomobject]@{ Name = 'Beta'; FolderName = "WinoMail_Beta_$version" } }
    if ($Selection.Sideload) { $sideloadChannels += [pscustomobject]@{ Name = 'Sideload'; FolderName = "WinoMail_SideloadRelease_$(([version]$version).ToString(3))" } }
    $destinations = @()
    if ($Selection.Store) { $destinations += Join-Path $outputRoot "WinoMail_Store_$version" }
    foreach ($channel in $sideloadChannels) { $destinations += Join-Path $outputRoot $channel.FolderName }
    Assert-ReleaseDestinations $destinations

    return [pscustomobject]@{
        Selection = $Selection; Version = $version; Project = $project
        RepositoryRoot = $RepositoryRoot; ManifestPath = $manifestPath
        ManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        StoreName = [string]$manifest.Package.Identity.Name
        StorePublisher = [string]$manifest.Package.Identity.Publisher
        OutputRoot = $outputRoot; Destinations = $destinations; SideloadChannels = $sideloadChannels
    }
}

function Get-ReleaseTools {
    param([object]$Selection)

    if (-not $IsWindows) { throw 'Release packaging requires Windows.' }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools and vswhere are required.' }
    $components = @('Microsoft.Component.MSBuild', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
    if ($Selection.Architectures -contains 'ARM64') { $components += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
    $installations = @(& $vswhere -latest -products '*' -requires @components -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installations.Count -ne 1) {
        throw 'Install Visual Studio Build Tools with MSBuild and the C++ tools for the selected architectures.'
    }
    $msbuild = Join-Path $installations[0] 'MSBuild/Current/Bin/amd64/MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild)) { throw 'The x64 MSBuild executable is missing.' }

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $required = @('makeappx.exe', 'makepri.exe')
    if ($Selection.Beta -or $Selection.Sideload) { $required += 'signtool.exe' }
    $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending | Where-Object {
            $directory = $_.FullName
            @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $directory "x64/$_")) }).Count -eq 0
        } | Select-Object -First 1
    if ($null -eq $sdk) { throw 'The Windows SDK packaging tools are missing.' }
    return [pscustomobject]@{
        # Use the repository-pinned SDK's MSBuild. VS 2022's SDK resolver cannot load .NET 10.
        MSBuild = $dotnet; MakeAppx = Join-Path $sdk.FullName 'x64/makeappx.exe'
        MakePri = Join-Path $sdk.FullName 'x64/makepri.exe'
        SignTool = if ($Selection.Beta -or $Selection.Sideload) { Join-Path $sdk.FullName 'x64/signtool.exe' } else { $null }
    }
}

function Get-ReleaseSigningConfiguration {
    param([switch]$IncludeBeta, [switch]$IncludeSideload)

    $configuration = @{}
    $settings = @{
        Endpoint = 'WINO_BETA_RELEASE_SIGNING_ENDPOINT'
        CodeSigningAccountName = 'WINO_BETA_RELEASE_SIGNING_ACCOUNT_NAME'
        CertificateProfileName = 'WINO_BETA_RELEASE_SIGNING_CERTIFICATE_PROFILE_NAME'
        PublisherSubject = 'WINO_BETA_RELEASE_PUBLISHER_SUBJECT'
    }
    foreach ($name in $settings.Keys) {
        $configuration[$name] = [Environment]::GetEnvironmentVariable($settings[$name])
        if ([string]::IsNullOrWhiteSpace($configuration[$name]) -or $configuration[$name] -match '[<>]') {
            throw "Set $($settings[$name]) before building a signed sideload release. See docs/local-script-environment.md."
        }
    }
    $endpoint = $null
    if (-not [Uri]::TryCreate($configuration.Endpoint, [UriKind]::Absolute, [ref]$endpoint) -or $endpoint.Scheme -ne 'https') {
        throw 'The signing endpoint must be an absolute HTTPS URL.'
    }
    if ($configuration.PublisherSubject -cne $script:SideloadPublisher) {
        throw 'The signing publisher differs from the existing beta publisher. Do not change the installed package identity.'
    }
    $credentials = @{}
    foreach ($name in @('AZURE_TENANT_ID', 'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET')) {
        $inputName = "WINO_BETA_RELEASE_$name"
        $value = [Environment]::GetEnvironmentVariable($inputName)
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Set $inputName before building a signed sideload release. See docs/local-script-environment.md." }
        $credentials[$name] = $value
    }

    $dlib = $env:WINO_BETA_RELEASE_SIGNING_DLIB_PATH
    if ([string]::IsNullOrWhiteSpace($dlib)) {
        $roots = @(
            (Join-Path $env:LOCALAPPDATA 'Microsoft/MicrosoftArtifactSigningClientTools'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft/ArtifactSigningClientTools'),
            (Join-Path $env:ProgramFiles 'Microsoft/ArtifactSigningClientTools'),
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/ArtifactSigningClientTools'),
            (Join-Path $env:ProgramFiles 'Microsoft/TrustedSigningClientTools')
        )
        $dlib = $roots | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
            Get-ChildItem -LiteralPath $_ -Filter Azure.CodeSigning.Dlib.dll -File -Recurse
        } | Where-Object { $_.FullName -notmatch '[\\/](x86|arm64)[\\/]' } |
            Sort-Object FullName | Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($dlib) -or -not (Test-Path -LiteralPath $dlib -PathType Leaf)) {
        throw 'Install Azure Artifact Signing Client Tools, or set WINO_BETA_RELEASE_SIGNING_DLIB_PATH to its x64 DLL.'
    }
    $distributions = @{}
    if ($IncludeBeta) {
        $distributions.Beta = Get-ReleaseDistributionConfiguration @{
            AppInstallerUri = $env:WINO_BETA_RELEASE_APPINSTALLER_URI
            PackageBaseUri = $env:WINO_BETA_RELEASE_PACKAGE_BASE_URI
        }
    }
    if ($IncludeSideload) {
        $distributions.Sideload = Get-ReleaseDistributionConfiguration @{
            AppInstallerUri = if ($env:WINO_SIDELOAD_RELEASE_APPINSTALLER_URI) { $env:WINO_SIDELOAD_RELEASE_APPINSTALLER_URI } else { 'http://download.winomail.app/WinoMail.appinstaller' }
            PackageBaseUri = $env:WINO_SIDELOAD_RELEASE_PACKAGE_BASE_URI
        }
    }
    if ($IncludeBeta -and $IncludeSideload -and $distributions.Beta.AppInstallerUri -eq $distributions.Sideload.AppInstallerUri) {
        throw 'Beta and stable sideload releases must use different App Installer feed URLs.'
    }
    return [pscustomobject]@{ Configuration = $configuration; Credentials = $credentials; Dlib = [IO.Path]::GetFullPath($dlib); Distributions = $distributions }
}

function Invoke-ReleaseTool {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPath, [hashtable]$SigningEnvironment = @{})

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $script:ReleaseRepositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $isMakePri = [IO.Path]::GetFileName($Executable) -ieq 'makepri.exe'
    if ($isMakePri) {
        $start.StandardOutputEncoding = [Text.Encoding]::Unicode
        $start.StandardErrorEncoding = [Text.Encoding]::Unicode
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # MSBuild imports environment variables as properties. Keep local settings and credentials out.
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -like 'AZURE_*' -or $key -like 'WINO_*' -or $key -like 'OPENAI_*') { $null = $start.Environment.Remove($key) }
    }
    foreach ($key in $SigningEnvironment.Keys) { $start.Environment[$key] = $SigningEnvironment[$key] }

    $null = New-Item -ItemType Directory -Path (Split-Path $LogPath) -Force
    [pscustomobject]@{ Executable = $Executable; Arguments = $Arguments; StartedUtc = [DateTime]::UtcNow } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$LogPath.command.json" -Encoding utf8
    Write-Host "Running $([IO.Path]::GetFileName($Executable)). Log: $LogPath"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $null = $process.Start()
        $started = $true
        if ($SigningEnvironment.Count -eq 0 -and -not $isMakePri) {
            $logStream = [IO.FileStream]::new($LogPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try {
                $stdout = $process.StandardOutput.BaseStream.CopyToAsync($logStream)
                $stderr = $process.StandardError.ReadToEndAsync()
                $process.WaitForExit()
                $null = $stdout.GetAwaiter().GetResult()
                $errorText = $stderr.GetAwaiter().GetResult()
            }
            finally { $logStream.Dispose() }
            [IO.File]::AppendAllText($LogPath, $errorText)
        }
        else {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            foreach ($value in $SigningEnvironment.Values) {
                if (-not [string]::IsNullOrEmpty($value)) { $output = $output.Replace($value, '[redacted]') }
            }
            [IO.File]::WriteAllText($LogPath, $output)
        }
        if ($process.ExitCode -ne 0) {
            Write-Host ((Get-Content -LiteralPath $LogPath -Tail 18) -join [Environment]::NewLine)
            throw "$([IO.Path]::GetFileName($Executable)) failed with exit code $($process.ExitCode). See $LogPath"
        }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Get-ReleaseBuildArguments {
    param([object]$Plan, [string]$Staging, [switch]$Restore)

    $arguments = @('msbuild', $Plan.Project, '-nologo', '-m', '-nr:false', '-verbosity:normal', '-p:Configuration=Release', '-p:Platform=x64')
    if ($Plan.Selection.Architectures.Count -eq 1) { $arguments += '-p:RuntimeIdentifiers=win-x64' }
    if ($Restore) {
        return $arguments + @('-t:Restore', "-p:RestoreConfigFile=$(Join-Path $Plan.RepositoryRoot 'nuget.config')")
    }
    $mode = if ($Plan.Selection.Store) { 'StoreUpload' } else { 'SideloadOnly' }
    return $arguments + @(
        '-t:Build', '-p:GenerateAppxPackageOnBuild=true', '-p:AppxBundle=Always',
        "-p:AppxBundlePlatforms=$($Plan.Selection.Architectures -join '|')", "-p:UapAppxPackageBuildMode=$mode",
        "-p:AppxPackageDir=$(Join-Path $Staging 'sdk')\", "-p:WinoReleaseStagingRoot=$(Join-Path $Staging 'exports')",
        '-p:AppxPackageSigningEnabled=false', '-p:GenerateTemporaryStoreCertificate=false',
        '-p:GenerateAppInstallerFile=false', '-p:AppxAutoIncrementPackageRevision=false',
        '-p:AppxSymbolPackageEnabled=true', '-p:GenerateTestArtifacts=true'
    )
}

function Get-ArchiveXml {
    param([string]$Path, [string]$EntryName)

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { throw "The package is missing $EntryName`: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-ReleaseIdentity {
    param([object]$Identity, [string]$Name, [string]$Publisher, [string]$Version)

    if ([string]$Identity.Name -cne $Name -or [string]$Identity.Publisher -cne $Publisher -or
        [string]$Identity.Version -cne $Version) {
        throw 'The generated package identity or version does not match the selected release.'
    }
}

function Get-PriCandidates {
    param([xml]$Dump, [string]$Layout)

    $items = [Collections.Generic.List[string]]::new()
    foreach ($resource in $Dump.SelectNodes("//*[local-name()='NamedResource']")) {
        $uri = [string]$resource.GetAttribute('uri')
        $resourceName = $uri -replace '^ms-resource://[^/]+/', ''
        foreach ($candidate in $resource.SelectNodes("*[local-name()='Candidate']")) {
            $value = $candidate.SelectSingleNode("*[local-name()='Value' or local-name()='Base64Value']")
            $qualifiers = @($candidate.SelectNodes(".//*[local-name()='Qualifier']") | ForEach-Object {
                '{0}={1}:{2}:{3}' -f $_.GetAttribute('name'), $_.GetAttribute('value'), $_.GetAttribute('priority'), $_.GetAttribute('scoreAsDefault')
            } | Sort-Object)
            if ($null -eq $value) { throw "The PRI candidate has no value: $resourceName" }
            if ($candidate.GetAttribute('type') -eq 'Path') {
                $resourcePath = Resolve-ReleaseChildPath $Layout $value.InnerText
                if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
                    throw "The PRI references a missing asset: $($value.InnerText)"
                }
            }
            $items.Add((@($resourceName, $candidate.GetAttribute('type'), ($qualifiers -join '|'), $value.InnerXml) | ConvertTo-Json -Compress))
        }
    }
    if ($items.Count -eq 0) { throw 'The application PRI contains no resource candidates.' }
    return @($items | Sort-Object)
}

function New-SideloadPackage {
    param([object]$Plan, [object]$Tools, [string]$Staging, [string]$Architecture)

    $export = Join-Path $Staging "exports/$Architecture"
    $source = Join-Path $export 'payload'
    foreach ($file in @('payload/AppxManifest.xml', 'application.pri', 'application-pri-path.txt', 'payload-paths.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $export $file) -PathType Leaf)) {
            throw "The $Architecture build did not export $file. Sideload packaging cannot recompile the application."
        }
    }
    foreach ($relative in Get-Content -LiteralPath (Join-Path $export 'payload-paths.txt')) {
        $payloadPath = Resolve-ReleaseChildPath $source $relative
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) { throw "The exported payload is incomplete: $relative" }
    }
    $layout = Join-Path $Staging "sideload-layouts/$Architecture"
    $null = New-Item -ItemType Directory -Path $layout -Force
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $layout -Recurse
    $manifestPath = Join-Path $layout 'AppxManifest.xml'
    $manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    Assert-ReleaseIdentity $manifest.Package.Identity $Plan.StoreName $Plan.StorePublisher $Plan.Version
    if ([string]$manifest.Package.Identity.ProcessorArchitecture -ine $Architecture) { throw 'The exported package architecture is incorrect.' }
    $manifest.Package.Identity.Name = $script:SideloadIdentityName
    $manifest.Package.Identity.Publisher = $script:SideloadPublisher
    $manifest.Save($manifestPath)

    $priRelative = (Get-Content -LiteralPath (Join-Path $export 'application-pri-path.txt') -Raw).Trim()
    $priPath = Resolve-ReleaseChildPath $layout $priRelative
    $priWork = Join-Path $Staging "pri/$Architecture"
    $priInput = Join-Path $priWork 'input'
    $null = New-Item -ItemType Directory -Path $priInput -Force
    Copy-Item -LiteralPath (Join-Path $export 'application.pri') -Destination (Join-Path $priInput 'application.pri')
    $config = Join-Path $priWork 'priconfig.xml'
    # Only index the original full application PRI. Library PRI files in the layout stay untouched.
    @'
<?xml version="1.0" encoding="utf-8"?>
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="." startIndexAt="">
    <default><qualifier name="Language" value="en-US" /></default>
    <indexer-config type="folder" foldernameAsQualifier="true" filenameAsQualifier="true" qualifierDelimiter="." />
    <indexer-config type="PRI" />
  </index>
</resources>
'@ | Set-Content -LiteralPath $config -Encoding utf8
    Invoke-ReleaseTool $Tools.MakePri @('new', '/pr', $priInput, '/cf', $config, '/in', $script:SideloadIdentityName, '/of', $priPath, '/o') (Join-Path $Staging "logs/pri-$Architecture.log")
    $beforeDump = Join-Path $priWork 'before.xml'
    $afterDump = Join-Path $priWork 'after.xml'
    Invoke-ReleaseTool $Tools.MakePri @('dump', '/if', (Join-Path $export 'application.pri'), '/of', $beforeDump, '/dt', 'detailed', '/o') (Join-Path $Staging "logs/pri-before-$Architecture.log")
    Invoke-ReleaseTool $Tools.MakePri @('dump', '/if', $priPath, '/of', $afterDump, '/dt', 'detailed', '/o') (Join-Path $Staging "logs/pri-after-$Architecture.log")
    $before = [xml](Get-Content -LiteralPath $beforeDump -Raw)
    $after = [xml](Get-Content -LiteralPath $afterDump -Raw)
    $map = $after.SelectSingleNode("//*[local-name()='ResourceMap']")
    if ($null -eq $map -or $map.GetAttribute('name') -cne $script:SideloadIdentityName) { throw 'The beta PRI has an incorrect resource-map identity.' }
    $beforeCandidates = @(Get-PriCandidates $before $source)
    $afterCandidates = @(Get-PriCandidates $after $layout)
    if (@(Compare-Object $beforeCandidates $afterCandidates -CaseSensitive).Count -gt 0) {
        throw "Sideload resource candidates differ from the compiled resources for $Architecture."
    }

    $package = Join-Path $Staging "sideload-packages/WinoMail_Sideload_$($Plan.Version)_$Architecture.msix"
    $null = New-Item -ItemType Directory -Path (Split-Path $package) -Force
    Invoke-ReleaseTool $Tools.MakeAppx @('pack', '/d', $layout, '/p', $package, '/h', 'SHA256') (Join-Path $Staging "logs/pack-$Architecture.log")
    return $package
}

function Assert-ReleaseBundle {
    param([string]$Bundle, [object]$Plan, [string]$Name, [string]$Publisher, [string]$InspectionRoot)

    $manifest = Get-ArchiveXml $Bundle 'AppxMetadata/AppxBundleManifest.xml'
    Assert-ReleaseIdentity $manifest.Bundle.Identity $Name $Publisher $Plan.Version
    $packages = @($manifest.Bundle.Packages.Package | Where-Object { $_.Type -eq 'application' })
    $actual = @($packages | ForEach-Object { [string]$_.Architecture } | Sort-Object)
    $expected = @($Plan.Selection.Architectures | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
    if ($actual.Count -ne $expected.Count -or @(Compare-Object $actual $expected).Count -gt 0) { throw 'The bundle does not contain exactly the selected architectures.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($Bundle)
    $hashes = @{}
    try {
        $null = New-Item -ItemType Directory -Path $InspectionRoot -Force
        foreach ($package in $packages) {
            $architecture = [string]$package.Architecture
            $path = Join-Path $InspectionRoot "$architecture.msix"
            $entry = $archive.GetEntry([string]$package.FileName)
            if ($null -eq $entry) { throw 'The bundle references a missing architecture package.' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
            $appManifest = Get-ArchiveXml $path 'AppxManifest.xml'
            Assert-ReleaseIdentity $appManifest.Package.Identity $Name $Publisher $Plan.Version
            if ([string]$appManifest.Package.Identity.ProcessorArchitecture -ine $architecture) { throw 'The inner package architecture is incorrect.' }
            $inner = [IO.Compression.ZipFile]::OpenRead($path)
            try {
                $binaryEntries = @($inner.Entries | Where-Object { $_.Name -match '\.(exe|dll)$' })
                if (@($binaryEntries | Where-Object { $_.Name -eq 'Wino.Mail.WinUI.exe' }).Count -ne 1) { throw 'The package has no Wino executable.' }
                foreach ($binary in $binaryEntries) {
                    $stream = $binary.Open()
                    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                    $relative = $binary.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
                    $source = Resolve-ReleaseChildPath (Join-Path (Split-Path (Split-Path $InspectionRoot)) "exports/$architecture/payload") $relative
                    if (-not (Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $hash) {
                        throw "The packaged binary differs from the compiled payload: $architecture/$relative"
                    }
                    $hashes["$architecture/$($binary.FullName)"] = $hash
                }
            }
            finally { $inner.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    return $hashes
}

function Get-StoreReleaseArtifact {
    param([object]$Plan, [string]$Staging)

    $uploads = @(Get-ChildItem -LiteralPath (Join-Path $Staging 'sdk') -Filter '*.msixupload' -File -Recurse)
    if ($uploads.Count -ne 1) { throw "Expected one Store upload file. Found $($uploads.Count)." }
    $archive = [IO.Compression.ZipFile]::OpenRead($uploads[0].FullName)
    try {
        $bundles = @($archive.Entries | Where-Object { $_.Name -match '\.(msixbundle|appxbundle)$' })
        $symbols = @($archive.Entries | Where-Object { $_.Name -match '\.appxsym$' })
        if ($bundles.Count -ne 1 -or $symbols.Count -eq 0) { throw 'The Store upload must contain one bundle and debug symbols.' }
        $bundlePath = Join-Path $Staging 'store.msixbundle'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($bundles[0], $bundlePath, $false)
    }
    finally { $archive.Dispose() }
    $hashes = Assert-ReleaseBundle $bundlePath $Plan $Plan.StoreName $Plan.StorePublisher (Join-Path $Staging 'inspect/store')
    $folder = Join-Path $Staging "ready/WinoMail_Store_$($Plan.Version)"
    $null = New-Item -ItemType Directory -Path $folder -Force
    Copy-Item -LiteralPath $uploads[0].FullName -Destination (Join-Path $folder "WinoMail_Store_$($Plan.Version).msixupload")
    return $hashes
}

function Copy-ReleaseDependencies {
    param([string]$SdkOutput, [string]$Destination)

    foreach ($directory in Get-ChildItem -LiteralPath $SdkOutput -Directory -Filter Dependencies -Recurse) {
        foreach ($file in Get-ChildItem -LiteralPath $directory.FullName -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($directory.FullName, $file.FullName)
            $target = Resolve-ReleaseChildPath $Destination $relative
            if (Test-Path -LiteralPath $target) {
                if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "Dependency packages conflict: $relative"
                }
                continue
            }
            $null = New-Item -ItemType Directory -Path (Split-Path $target) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

function Get-ReleaseDistributionConfiguration {
    param([hashtable]$Configuration)

    $installerText = if ($Configuration['AppInstallerUri']) { $Configuration['AppInstallerUri'] } else { 'http://download.winomail.app/WinoMailBeta.appinstaller' }
    $baseText = if ($Configuration['PackageBaseUri']) { $Configuration['PackageBaseUri'] } else { ([uri]::new([uri]$installerText, '.')).AbsoluteUri }
    $uris = @{}
    foreach ($entry in @{ AppInstallerUri = $installerText; PackageBaseUri = $baseText }.GetEnumerator()) {
        $uri = $null
        if (-not [uri]::TryCreate($entry.Value, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -notin @('http', 'https') -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
            throw "$($entry.Key) must be an absolute HTTP or HTTPS URL without credentials, query parameters, or a fragment."
        }
        $uris[$entry.Key] = $uri
    }
    $fileName = [Uri]::UnescapeDataString($uris.AppInstallerUri.Segments[-1])
    if ($fileName -notmatch '^[A-Za-z0-9_.-]+\.appinstaller$') { throw 'AppInstallerUri must end with an .appinstaller filename.' }
    if (-not $uris.PackageBaseUri.AbsolutePath.EndsWith('/')) { throw 'PackageBaseUri must end with a slash.' }
    return [pscustomobject]@{ AppInstallerUri = $uris.AppInstallerUri; PackageBaseUri = $uris.PackageBaseUri; FileName = $fileName }
}

function Get-ReleaseDownloadUri {
    param([uri]$BaseUri, [string]$RelativePath)

    $escaped = ($RelativePath.Replace('\', '/').Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
    return [uri]::new($BaseUri, $escaped).AbsoluteUri
}

function New-SideloadAppInstaller {
    param([string]$Bundle, [object]$Plan, [object]$Distribution)

    $manifest = Get-ArchiveXml $Bundle 'AppxMetadata/AppxBundleManifest.xml'
    Assert-ReleaseIdentity $manifest.Bundle.Identity $script:SideloadIdentityName $script:SideloadPublisher $Plan.Version
    $folder = Split-Path $Bundle
    $folderName = Split-Path $folder -Leaf
    $namespace = 'http://schemas.microsoft.com/appx/appinstaller/2017/2'
    $document = [xml]::new()
    $root = $document.CreateElement('AppInstaller', $namespace)
    $root.SetAttribute('Version', $Plan.Version)
    $root.SetAttribute('Uri', $Distribution.AppInstallerUri.AbsoluteUri)
    $null = $document.AppendChild($root)
    $main = $document.CreateElement('MainBundle', $namespace)
    foreach ($attribute in @('Name', 'Publisher', 'Version')) { $main.SetAttribute($attribute, $manifest.Bundle.Identity.GetAttribute($attribute)) }
    $main.SetAttribute('Uri', (Get-ReleaseDownloadUri $Distribution.PackageBaseUri "$folderName/$([IO.Path]::GetFileName($Bundle))"))
    $null = $root.AppendChild($main)

    $dependencyRoot = Join-Path $folder 'Dependencies'
    if (Test-Path -LiteralPath $dependencyRoot) {
        $dependencies = @{}
        foreach ($file in Get-ChildItem -LiteralPath $dependencyRoot -File -Recurse | Where-Object { $_.Extension -in @('.msix', '.appx') }) {
            $dependencyManifest = Get-ArchiveXml $file.FullName 'AppxManifest.xml'
            $identity = $dependencyManifest.Package.Identity
            if ([string]$identity.ProcessorArchitecture -notin ($Plan.Selection.Architectures + @('neutral'))) { continue }
            $key = '{0}|{1}|{2}' -f $identity.Name, $identity.Publisher, $identity.ProcessorArchitecture
            if (-not $dependencies.ContainsKey($key) -or [version]$dependencies[$key].Identity.Version -lt [version]$identity.Version) {
                $dependencies[$key] = @{ Identity = $identity; Path = $file.FullName }
            }
        }
        if ($dependencies.Count -gt 0) {
            $node = $document.CreateElement('Dependencies', $namespace)
            foreach ($key in $dependencies.Keys | Sort-Object) {
                $dependency = $dependencies[$key]
                $package = $document.CreateElement('Package', $namespace)
                foreach ($attribute in @('Name', 'Publisher', 'Version', 'ProcessorArchitecture')) { $package.SetAttribute($attribute, $dependency.Identity.GetAttribute($attribute)) }
                $relative = [IO.Path]::GetRelativePath($folder, $dependency.Path)
                $package.SetAttribute('Uri', (Get-ReleaseDownloadUri $Distribution.PackageBaseUri "$folderName/$relative"))
                $null = $node.AppendChild($package)
            }
            $null = $root.AppendChild($node)
        }
    }
    # The 2017/2 schema supports the application's Windows 10 1809 minimum.
    $updates = $document.CreateElement('UpdateSettings', $namespace)
    $onLaunch = $document.CreateElement('OnLaunch', $namespace)
    $onLaunch.SetAttribute('HoursBetweenUpdateChecks', '4')
    $null = $updates.AppendChild($onLaunch)
    $null = $updates.AppendChild($document.CreateElement('AutomaticBackgroundTask', $namespace))
    $null = $root.AppendChild($updates)
    $path = Join-Path $folder $Distribution.FileName
    $document.Save($path)
    $saved = [xml](Get-Content -LiteralPath $path -Raw)
    Assert-ReleaseIdentity $saved.AppInstaller.MainBundle $script:SideloadIdentityName $script:SideloadPublisher $Plan.Version
    if ($saved.AppInstaller.Uri -cne $Distribution.AppInstallerUri.AbsoluteUri -or
        $saved.AppInstaller.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks -ne '4' -or
        $null -eq $saved.SelectSingleNode("//*[local-name()='AutomaticBackgroundTask']")) {
        throw 'The App Installer update settings are invalid.'
    }
    Write-Host "Update feed: $($Distribution.AppInstallerUri.AbsoluteUri)"
}

function Sign-SideloadRelease {
    param([string]$Bundle, [object]$Plan, [object]$Tools, [object]$Signing, [string]$Staging)

    $manifest = Get-ArchiveXml $Bundle 'AppxMetadata/AppxBundleManifest.xml'
    Assert-ReleaseIdentity $manifest.Bundle.Identity $script:SideloadIdentityName $Signing.Configuration.PublisherSubject $Plan.Version
    $metadata = @{
        Endpoint = $Signing.Configuration.Endpoint
        CodeSigningAccountName = $Signing.Configuration.CodeSigningAccountName
        CertificateProfileName = $Signing.Configuration.CertificateProfileName
        CorrelationId = [guid]::NewGuid().ToString()
        ExcludeCredentials = @('ManagedIdentityCredential', 'WorkloadIdentityCredential', 'SharedTokenCacheCredential',
            'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzureCliCredential', 'AzurePowerShellCredential',
            'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    }
    $metadataPath = Join-Path $Staging 'signing-metadata.json'
    $metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
    try {
        Invoke-ReleaseTool $Tools.SignTool @('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.acs.microsoft.com', '/td', 'SHA256',
            '/dlib', $Signing.Dlib, '/dmdf', $metadataPath, $Bundle) (Join-Path $Staging 'logs/sign.log') $Signing.Credentials
        Invoke-ReleaseTool $Tools.SignTool @('verify', '/pa', '/all', '/v', '/tw', $Bundle) (Join-Path $Staging 'logs/verify-signature.log')
    }
    finally { Remove-Item -LiteralPath $metadataPath -ErrorAction SilentlyContinue }
}

function Complete-ReleaseOutputs {
    param([object]$Plan, [string]$Staging)

    Assert-ReleaseDestinations $Plan.Destinations
    $moved = [Collections.Generic.List[object]]::new()
    try {
        foreach ($destination in $Plan.Destinations) {
            $name = Split-Path $destination -Leaf
            $source = Resolve-ReleaseChildPath $Staging "ready/$name"
            $null = Resolve-ReleaseChildPath $Plan.OutputRoot $name
            [IO.Directory]::Move($source, $destination)
            $moved.Add([pscustomobject]@{ Source = $source; Destination = $destination })
        }
    }
    catch {
        foreach ($item in $moved) { [IO.Directory]::Move($item.Destination, $item.Source) }
        throw
    }
}

function Remove-ReleaseStaging {
    param([object]$Plan, [string]$Staging)

    $stagingRoot = Resolve-ReleaseChildPath $Plan.OutputRoot '.staging'
    $runName = [IO.Path]::GetFileName([IO.Path]::GetFullPath($Staging))
    $expectedPath = Resolve-ReleaseChildPath $stagingRoot $runName
    if ($runName -notmatch '^[a-f0-9]{32}$' -or
        [IO.Path]::GetFullPath($Staging) -ine $expectedPath) {
        throw 'Cleanup requires the current release run directory under AppPackages/.staging.'
    }
    foreach ($path in @($stagingRoot, $expectedPath)) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Release staging cleanup cannot follow a directory link.'
        }
    }
    Remove-Item -LiteralPath $expectedPath -Recurse -Force
    # Keep diagnostics from earlier failed runs. Remove the parent only when empty.
    if (@(Get-ChildItem -LiteralPath $stagingRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $stagingRoot -Force
    }
}

function Invoke-ReleaseBuild {
    param([object]$Plan, [object]$Tools, [object]$Signing)

    $null = New-Item -ItemType Directory -Path $Plan.OutputRoot -Force
    $lock = $null
    $staging = $null
    $stage = 'acquire release lock'
    try {
        $lockPath = Join-Path $Plan.OutputRoot '.release.lock'
        $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 4096, [IO.FileOptions]::DeleteOnClose)
        Assert-ReleaseDestinations $Plan.Destinations
        $staging = Join-Path $Plan.OutputRoot ('.staging/' + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $staging -Force

        $stage = 'restore'
        Invoke-ReleaseTool $Tools.MSBuild (Get-ReleaseBuildArguments $Plan $staging -Restore) (Join-Path $staging 'logs/restore.log')
        $stage = 'Release compilation and SDK packaging'
        Invoke-ReleaseTool $Tools.MSBuild (Get-ReleaseBuildArguments $Plan $staging) (Join-Path $staging 'logs/build.log')
        $storeHashes = $null
        if ($Plan.Selection.Store) {
            $stage = 'Store verification'
            $storeHashes = Get-StoreReleaseArtifact $Plan $staging
        }
        if ($Plan.SideloadChannels.Count -gt 0) {
            $stage = 'sideload packaging'
            foreach ($architecture in $Plan.Selection.Architectures) { $null = New-SideloadPackage $Plan $Tools $staging $architecture }
            $bundle = Join-Path $staging 'sideload.msixbundle'
            Invoke-ReleaseTool $Tools.MakeAppx @('bundle', '/d', (Join-Path $staging 'sideload-packages'), '/p', $bundle, '/bv', $Plan.Version) (Join-Path $staging 'logs/bundle.log')
            $stage = 'sideload verification'
            $sideloadHashes = Assert-ReleaseBundle $bundle $Plan $script:SideloadIdentityName $script:SideloadPublisher (Join-Path $staging 'inspect/sideload')
            if ($null -ne $storeHashes) {
                if ($sideloadHashes.Count -ne $storeHashes.Count) { throw 'The channels contain different binary sets.' }
                foreach ($key in $sideloadHashes.Keys) {
                    if (-not $storeHashes.ContainsKey($key) -or $storeHashes[$key] -cne $sideloadHashes[$key]) { throw "The channels contain different binaries: $key" }
                }
            }
            $stage = 'Azure signing'
            Sign-SideloadRelease $bundle $Plan $Tools $Signing $staging
            $signedHash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
            foreach ($channel in $Plan.SideloadChannels) {
                $stage = "$($channel.Name) output and App Installer generation"
                $folder = Join-Path $staging "ready/$($channel.FolderName)"
                $null = New-Item -ItemType Directory -Path $folder -Force
                $channelBundle = Join-Path $folder "$($channel.FolderName).msixbundle"
                Copy-Item -LiteralPath $bundle -Destination $channelBundle
                if ((Get-FileHash -LiteralPath $channelBundle -Algorithm SHA256).Hash -cne $signedHash) {
                    throw 'The sideload copy differs from the verified signed bundle.'
                }
                Copy-ReleaseDependencies (Join-Path $staging 'sdk') (Join-Path $folder 'Dependencies')
                New-SideloadAppInstaller $channelBundle $Plan $Signing.Distributions[$channel.Name]
            }
        }
        $stage = 'finalize outputs'
        if ((Get-FileHash -LiteralPath $Plan.ManifestPath -Algorithm SHA256).Hash -cne $Plan.ManifestHash) {
            throw 'The source manifest changed during the build. The outputs remain in staging.'
        }
        Complete-ReleaseOutputs $Plan $staging
        $stage = 'staging cleanup (release outputs are finalized)'
        Remove-ReleaseStaging $Plan $staging
        Write-Host 'Release packages are ready:' -ForegroundColor Green
        $Plan.Destinations | ForEach-Object { Write-Host $_ }
    }
    catch {
        throw "Release failed during '$stage'. Staging: $staging`n$($_.Exception.Message)"
    }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}

function Invoke-InteractiveRelease {
    $selection = Read-ReleaseSelection
    if ($null -eq $selection) { Write-Host 'No channels selected. Nothing to build.'; return }
    $plan = New-ReleasePlan $selection
    Write-Host "Version: $($plan.Version) | Store: $($selection.Store) | Beta: $($selection.Beta) | Stable sideload: $($selection.Sideload) | Architectures: $($selection.Architectures -join ', ')"
    $plan.Destinations | ForEach-Object { Write-Host "Output: $_" }
    $tools = Get-ReleaseTools $selection
    $signing = if ($selection.Beta -or $selection.Sideload) { Get-ReleaseSigningConfiguration -IncludeBeta:$selection.Beta -IncludeSideload:$selection.Sideload } else { $null }
    Invoke-ReleaseBuild $plan $tools $signing
    try { Invoke-Item -LiteralPath $plan.OutputRoot } catch { Write-Warning 'Packages are ready, but Explorer could not open the output folder.' }
}

# Dot-sourcing exposes functions for the script tests without prompting or building.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-InteractiveRelease } catch { Write-Error $_ -ErrorAction Continue; exit 1 }
}
