[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'Wino.Mail.Uwp\Wino.Mail.Uwp.csproj'
$manifestPath = Join-Path $repositoryRoot 'Wino.Mail.Uwp\Package.appxmanifest'
$runtimeIdentifier = switch ($Platform) {
    'x86' { 'win-x86' }
    'x64' { 'win-x64' }
    'arm64' { 'win-arm64' }
}

$visualStudioRoot = Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\18\Insiders'
$msbuildPath = Join-Path $visualStudioRoot 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuildPath)) {
    throw 'Visual Studio 2026 Insiders with the UWP tools workload is required.'
}

$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if ($null -eq $winapp) {
    throw 'The winapp CLI is required to create the MSIX package.'
}

Push-Location $repositoryRoot
try {
    & dotnet restore $projectPath --configfile (Join-Path $repositoryRoot 'nuget.config') "-p:Platform=$Platform" "-p:RuntimeIdentifier=$runtimeIdentifier"
    if ($LASTEXITCODE -ne 0) {
        throw "UWP restore failed with exit code $LASTEXITCODE."
    }

    & $msbuildPath $projectPath /t:Build "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:RuntimeIdentifier=$runtimeIdentifier" /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "UWP build failed with exit code $LASTEXITCODE."
    }

    $buildOutput = Join-Path $repositoryRoot "Wino.Mail.Uwp\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\$runtimeIdentifier"
    # Keep the manifest beside the already-filtered package layout. Pointing
    # winapp at the project manifest makes its asset discovery copy source-only
    # legacy qualifier aliases (for example targetsize-24_altform-unplated)
    # back into staging, where they collide with the canonical equivalents.
    $layoutManifestPath = Join-Path $buildOutput 'AppxManifest.xml'
    Copy-Item -LiteralPath $manifestPath -Destination $layoutManifestPath -Force
    $outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
        [IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
    }

    [void](New-Item -ItemType Directory -Path $outputRoot -Force)
    [xml]$manifest = Get-Content -LiteralPath $manifestPath
    $packageVersion = $manifest.Package.Identity.Version
    $packagePath = Join-Path $outputRoot "Wino.Mail.Uwp_${packageVersion}_${Platform}_${Configuration}.msix"
    if (Test-Path -LiteralPath $packagePath) {
        throw "Package already exists: $packagePath. Remove it explicitly before rebuilding."
    }

    & $winapp.Source package $buildOutput --manifest $layoutManifestPath --output $packagePath --exe Wino.Mail.Uwp.exe --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX packaging failed with exit code $LASTEXITCODE."
    }

    Write-Output $packagePath
}
finally {
    Pop-Location
}
