#requires -Version 5.1

<#
.SYNOPSIS
Signs an existing Windows application package with Azure Artifact Signing.

.DESCRIPTION
The script opens the repository artifacts directory, lists its folders from
newest to oldest, and prompts for a folder. It then finds the package in that
folder and signs it through the configured Azure Artifact Signing profile.

If the selected folder contains more than one supported package, the script
prompts for the package to sign. Use FolderPath and PackagePath to bypass the
interactive prompts in automation.

.PARAMETER ConfigPath
Specifies the JSON file that contains the Artifact Signing configuration.

.PARAMETER Endpoint
Overrides the Artifact Signing endpoint from the configuration file.

.PARAMETER CodeSigningAccountName
Overrides the Artifact Signing account name from the configuration file.

.PARAMETER CertificateProfileName
Overrides the certificate profile name from the configuration file.

.PARAMETER PublisherSubject
Overrides the exact certificate-profile subject used to validate the package
publisher before signing.

.PARAMETER ArtifactsPath
Overrides the repository artifacts directory.

.PARAMETER FolderPath
Selects a folder without an interactive prompt. An absolute path can be outside
the repository artifacts directory.

.PARAMETER PackagePath
Selects a package without an interactive package prompt.

.EXAMPLE
Copy-Item .\scripts\sideload-signing.sample.json .\scripts\sideload-signing.local.json
pwsh .\scripts\sign-artifact-package.ps1

.EXAMPLE
pwsh .\scripts\sign-artifact-package.ps1 -FolderPath .\2.0.51.0 -PackagePath .\WinoMail.msixbundle
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'sideload-signing.local.json'),
    [string]$Endpoint,
    [string]$CodeSigningAccountName,
    [string]$CertificateProfileName,
    [string]$PublisherSubject,
    [string]$ArtifactsPath,
    [string]$FolderPath,
    [string]$PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-ConfiguredValue {
    param(
        [string]$ParameterValue,
        [object]$Configuration,
        [Parameter(Mandatory)]
        [string]$PropertyName
    )

    if (-not [string]::IsNullOrWhiteSpace($ParameterValue)) {
        return $ParameterValue.Trim()
    }

    if ($null -ne $Configuration) {
        $property = $Configuration.PSObject.Properties[$PropertyName]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return ([string]$property.Value).Trim()
        }
    }

    return $null
}

function Assert-Command {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$InstallCommand
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "The '$Name' command is not available. Install it with: $InstallCommand"
    }

    return $command.Source
}

function Find-WindowsSdkTool {
    param(
        [Parameter(Mandatory)]
        [string]$FileName
    )

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $windowsKitsRoot -PathType Container)) {
        throw 'Windows SDK tools are not installed. Install Visual Studio 2022 with the Windows application development workload.'
    }

    $tool = Get-ChildItem -LiteralPath $windowsKitsRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName "x64\$FileName") -ErrorAction SilentlyContinue } |
        Select-Object -First 1

    if ($null -eq $tool) {
        throw "The Windows SDK tool '$FileName' is not installed. Install Visual Studio 2022 with the Windows application development workload."
    }

    return $tool.FullName
}

function Find-ArtifactSigningDlib {
    $overridePath = $env:AZURE_ARTIFACT_SIGNING_DLIB_PATH
    if (-not [string]::IsNullOrWhiteSpace($overridePath)) {
        $resolvedOverride = Get-FullPath -Path $overridePath -BasePath (Get-Location).Path
        if (Test-Path -LiteralPath $resolvedOverride -PathType Leaf) {
            return $resolvedOverride
        }

        throw "AZURE_ARTIFACT_SIGNING_DLIB_PATH does not point to a file: $resolvedOverride"
    }

    $clientRoots = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\ArtifactSigningClientTools'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\ArtifactSigningTools'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\ArtifactSigningClientTools'),
        (Join-Path $env:ProgramFiles 'Microsoft\ArtifactSigningClientTools'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\TrustedSigningClientTools'),
        (Join-Path $env:ProgramFiles 'Microsoft\TrustedSigningClientTools')
    ) | Select-Object -Unique

    foreach ($clientRoot in $clientRoots) {
        if (-not (Test-Path -LiteralPath $clientRoot -PathType Container)) {
            continue
        }

        $dlib = Get-ChildItem -LiteralPath $clientRoot -Filter 'Azure.CodeSigning.Dlib.dll' -File -Recurse |
            Sort-Object { if ($_.FullName -match '\\x64\\') { 0 } else { 1 } }, FullName |
            Select-Object -First 1

        if ($null -ne $dlib) {
            Write-Verbose "Using the Artifact Signing dlib at '$($dlib.FullName)'."
            return $dlib.FullName
        }
    }

    throw 'Azure Artifact Signing Client Tools are not installed. Install them with: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools'
}

function Read-NumberedSelection {
    param(
        [Parameter(Mandatory)]
        [object[]]$Items,

        [Parameter(Mandatory)]
        [string]$Prompt
    )

    while ($true) {
        $selection = Read-Host $Prompt
        $index = 0
        if ([int]::TryParse($selection, [ref]$index) -and $index -ge 1 -and $index -le $Items.Count) {
            return $Items[$index - 1]
        }

        Write-Warning "Enter a number from 1 to $($Items.Count)."
    }
}

function Assert-PathWithinRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $requiredPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be inside the artifacts directory: $fullRoot"
    }
}

function Get-PackageIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $isBundle = [System.IO.Path]::GetExtension($Path).ToLowerInvariant() -in @('.appxbundle', '.msixbundle')
        $manifestPath = if ($isBundle) { 'AppxMetadata/AppxBundleManifest.xml' } else { 'AppxManifest.xml' }
        $manifestEntry = $archive.Entries |
            Where-Object { $_.FullName.Equals($manifestPath, [System.StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "The package does not contain $manifestPath."
        }

        $stream = $manifestEntry.Open()
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            $manifest = [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }

        $identity = if ($isBundle) { $manifest.Bundle.Identity } else { $manifest.Package.Identity }
        return [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [string]$identity.Version
        }
    }
    finally {
        $archive.Dispose()
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $ArtifactsPath = Join-Path $repositoryRoot 'artifacts'
}

$resolvedArtifactsPath = Get-FullPath -Path $ArtifactsPath -BasePath $repositoryRoot
if (-not (Test-Path -LiteralPath $resolvedArtifactsPath -PathType Container)) {
    throw "The artifacts directory does not exist: $resolvedArtifactsPath"
}

$configuration = $null
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $resolvedConfigPath = Get-FullPath -Path $ConfigPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
        throw "The signing configuration does not exist: $resolvedConfigPath`nCreate it with: Copy-Item .\scripts\sideload-signing.sample.json .\scripts\sideload-signing.local.json"
    }

    try {
        $configuration = Get-Content -LiteralPath $resolvedConfigPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "The signing configuration is not valid JSON: $resolvedConfigPath`n$($_.Exception.Message)"
    }
}

$Endpoint = Get-ConfiguredValue -ParameterValue $Endpoint -Configuration $configuration -PropertyName 'Endpoint'
$CodeSigningAccountName = Get-ConfiguredValue -ParameterValue $CodeSigningAccountName -Configuration $configuration -PropertyName 'CodeSigningAccountName'
$CertificateProfileName = Get-ConfiguredValue -ParameterValue $CertificateProfileName -Configuration $configuration -PropertyName 'CertificateProfileName'
$PublisherSubject = Get-ConfiguredValue -ParameterValue $PublisherSubject -Configuration $configuration -PropertyName 'PublisherSubject'

foreach ($requiredValue in @(
    @{ Name = 'Endpoint'; Value = $Endpoint },
    @{ Name = 'CodeSigningAccountName'; Value = $CodeSigningAccountName },
    @{ Name = 'CertificateProfileName'; Value = $CertificateProfileName },
    @{ Name = 'PublisherSubject'; Value = $PublisherSubject }
)) {
    if ([string]::IsNullOrWhiteSpace($requiredValue.Value) -or $requiredValue.Value -match '^<.+>$') {
        throw "The '$($requiredValue.Name)' Artifact Signing setting is required."
    }
}

$endpointUri = $null
if (-not [System.Uri]::TryCreate($Endpoint, [System.UriKind]::Absolute, [ref]$endpointUri) -or
    $endpointUri.Scheme -ne 'https') {
    throw 'Endpoint must be an absolute HTTPS URL.'
}

$azPath = Assert-Command -Name 'az' -InstallCommand 'winget install -e --id Microsoft.AzureCLI'
$azureAccountJson = & $azPath account show --only-show-errors --output json 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'Azure CLI is not authenticated. Run: az login'
}
$azureAccount = $azureAccountJson | ConvertFrom-Json
$azureIdentity = [string]$azureAccount.user.name

$signToolPath = Find-WindowsSdkTool -FileName 'signtool.exe'
$artifactSigningDlibPath = Find-ArtifactSigningDlib
$supportedExtensions = @('.appx', '.appxbundle', '.msix', '.msixbundle')

Push-Location -LiteralPath $resolvedArtifactsPath
try {
    if ([string]::IsNullOrWhiteSpace($FolderPath)) {
        $folders = @(Get-ChildItem -LiteralPath $resolvedArtifactsPath -Directory |
            Sort-Object @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, Name)

        if ($folders.Count -eq 0) {
            throw "No folders exist in the artifacts directory: $resolvedArtifactsPath"
        }

        Write-Host "Artifacts folders (newest first):"
        for ($index = 0; $index -lt $folders.Count; $index++) {
            Write-Host ('  [{0}] {1}  ({2})' -f ($index + 1), $folders[$index].Name, $folders[$index].LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
        }

        $selectedFolder = Read-NumberedSelection -Items $folders -Prompt 'Select a folder'
    }
    else {
        $resolvedFolderPath = Get-FullPath -Path $FolderPath -BasePath $resolvedArtifactsPath
        if (-not (Test-Path -LiteralPath $resolvedFolderPath -PathType Container)) {
            throw "The selected folder does not exist: $resolvedFolderPath"
        }

        $selectedFolder = Get-Item -LiteralPath $resolvedFolderPath
    }

    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $allPackages = @(Get-ChildItem -LiteralPath $selectedFolder.FullName -File -Recurse |
            Where-Object {
                $supportedExtensions -contains $_.Extension.ToLowerInvariant() -and
                $_.FullName -notmatch '[\\/]Dependencies[\\/]'
            })
        $bundlePackages = @($allPackages | Where-Object { $_.Extension.ToLowerInvariant() -in @('.appxbundle', '.msixbundle') })
        if ($bundlePackages.Count -gt 0) {
            $packageCandidates = $bundlePackages
        }
        else {
            $packageCandidates = $allPackages
        }

        $packages = @($packageCandidates |
            Sort-Object @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, Name)

        if ($packages.Count -eq 0) {
            throw "No APPX or MSIX package exists in: $($selectedFolder.FullName)"
        }

        if ($packages.Count -eq 1) {
            $selectedPackage = $packages[0]
        }
        else {
            Write-Host ''
            Write-Host "Packages in '$($selectedFolder.Name)' (newest first):"
            for ($index = 0; $index -lt $packages.Count; $index++) {
                $relativePath = $packages[$index].FullName.Substring($selectedFolder.FullName.Length).TrimStart('\')
                Write-Host ('  [{0}] {1}  ({2})' -f ($index + 1), $relativePath, $packages[$index].LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
            }

            $selectedPackage = Read-NumberedSelection -Items $packages -Prompt 'Select a package'
        }
    }
    else {
        $resolvedPackagePath = Get-FullPath -Path $PackagePath -BasePath $selectedFolder.FullName
        Assert-PathWithinRoot -Path $resolvedPackagePath -Root $selectedFolder.FullName -Description 'PackagePath'
        if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
            throw "The selected package does not exist: $resolvedPackagePath"
        }

        $selectedPackage = Get-Item -LiteralPath $resolvedPackagePath
        if ($supportedExtensions -notcontains $selectedPackage.Extension.ToLowerInvariant()) {
            throw "PackagePath must identify an APPX or MSIX package: $resolvedPackagePath"
        }
    }

    Write-Host ''
    Write-Host "Selected package: $($selectedPackage.FullName)"

    $packageIdentity = Get-PackageIdentity -Path $selectedPackage.FullName
    Write-Verbose "Package identity: $($packageIdentity.Name), publisher: $($packageIdentity.Publisher), version: $($packageIdentity.Version)"
    if (-not $packageIdentity.Publisher.Equals($PublisherSubject, [System.StringComparison]::Ordinal)) {
        throw "The package publisher does not match the Artifact Signing certificate subject. Package: '$($packageIdentity.Publisher)'. Certificate: '$PublisherSubject'. Rebuild the package with the certificate subject as Identity.Publisher, then run this script again."
    }

    if (-not $PSCmdlet.ShouldProcess($selectedPackage.FullName, 'Sign with Azure Artifact Signing')) {
        return
    }

    $metadataPath = Join-Path ([System.IO.Path]::GetTempPath()) ("wino-artifact-signing-{0}.json" -f [guid]::NewGuid().ToString('N'))
    try {
        $metadata = [ordered]@{
            Endpoint = $Endpoint
            CodeSigningAccountName = $CodeSigningAccountName
            CertificateProfileName = $CertificateProfileName
            CorrelationId = [guid]::NewGuid().ToString()
        }
        $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json), $utf8WithoutBom)

        $signOutput = @(& $signToolPath sign /v /debug /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $artifactSigningDlibPath /dmdf $metadataPath $selectedPackage.FullName 2>&1)
        $signExitCode = $LASTEXITCODE
        $signOutputText = $signOutput -join [Environment]::NewLine
        if ($signExitCode -ne 0) {
            if ($signOutputText -match 'Status:\s*403\s*\(Forbidden\)') {
                throw "Azure Artifact Signing denied the request (403) for account '$CodeSigningAccountName' and profile '$CertificateProfileName'. Verify those configuration values and assign the 'Artifact Signing Certificate Profile Signer' role to '$azureIdentity' at the account or certificate-profile scope."
            }

            if ($signOutputText -match '0x8007000b') {
                throw "SignTool rejected the package format or publisher (0x8007000B). Confirm that the package block-map hash uses SHA-256 and Identity.Publisher is exactly '$PublisherSubject'."
            }

            throw "Azure Artifact Signing failed with exit code $signExitCode.`n$signOutputText"
        }
        $signOutput | ForEach-Object { Write-Host $_ }

        & $signToolPath verify /pa /all /v $selectedPackage.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "The signed package failed SignTool verification. Exit code: $LASTEXITCODE"
        }
    }
    finally {
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            Remove-Item -LiteralPath $metadataPath -Force
        }
    }

    Write-Host ''
    Write-Host "Signed and verified: $($selectedPackage.FullName)" -ForegroundColor Green
}
finally {
    Pop-Location
}
