# Shared, read-only deployment checks. Dot-source this file from the harness or UI runner.
function Get-WinoDebugAssessment {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Publisher,
        [Parameter(Mandatory)][string]$ManifestVersion,
        [AllowEmptyString()][string]$WinAppVersion,
        [AllowEmptyCollection()][object[]]$InstalledPackages = @()
    )

    $code = 'Ready'
    $reason = 'WinApp project mode can deploy the checked-in Debug identity.'
    $next = './scripts/wino.ps1 run app'
    $parsedVersion = $null
    $packages = @($InstalledPackages | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Publisher = $_.Publisher
            Version = [string]$_.Version
            PackageFamilyName = $_.PackageFamilyName
            InstallLocation = $_.InstallLocation
            IsDevelopmentMode = [bool]$_.IsDevelopmentMode
            SignatureKind = [string]$_.SignatureKind
        }
    })

    if (-not [version]::TryParse($WinAppVersion, [ref]$parsedVersion) -or $parsedVersion -lt [version]'0.6.0') {
        $code = 'WinAppUnavailable'
        $reason = 'WinApp CLI 0.6 or later is required.'
        $next = 'Install or repair WinApp CLI, then rerun doctor.'
    }
    elseif (@($packages | Where-Object { $_.Name -ne $Name -or $_.Publisher -ne $Publisher }).Count -gt 0) {
        $code = 'IdentityMismatch'
        $reason = 'The installed package name or publisher does not match the checked-in manifest.'
        $next = 'Resolve the identity mismatch without rewriting the manifest or removing app data.'
    }
    elseif (@($packages | Where-Object { -not $_.IsDevelopmentMode }).Count -gt 0) {
        $code = 'InstalledPackageConflict'
        $reason = 'A signed non-development installation owns this identity. WinApp refuses to replace it with a Debug registration.'
        $next = 'Use a dedicated Windows development VM without this installed package. Keep the existing installation and data intact. Changing the current user installation requires an explicit migration decision.'
    }
    elseif ($packages.Count -gt 1) {
        $code = 'AmbiguousRegistration'
        $reason = 'More than one package registration matches the requested identity.'
        $next = 'Inspect the registrations before deployment.'
    }

    [pscustomobject]@{
        Ready = $code -eq 'Ready'
        Code = $code
        Reason = $reason
        NextAction = $next
        ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
        WinAppVersion = $WinAppVersion
        Manifest = [pscustomobject]@{ Name = $Name; Publisher = $Publisher; Version = $ManifestVersion }
        InstalledPackages = $packages
    }
}

function Get-WinoDebugReadiness {
    param([Parameter(Mandatory)][string]$ProjectPath, [string]$WinAppVersion)

    $manifestPath = Join-Path (Split-Path $ProjectPath) 'Package.appxmanifest'
    $manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop)
    $versionText = ''
    if ($PSBoundParameters.ContainsKey('WinAppVersion')) {
        $versionText = $WinAppVersion
    }
    elseif (Get-Command winapp -ErrorAction SilentlyContinue) {
        $versionOutput = & winapp --version
        if ($LASTEXITCODE -eq 0) { $versionText = ($versionOutput -join '').Trim() }
    }

    # Query failure must stop the caller; an unavailable query is not an empty registration list.
    $installed = @(Get-AppxPackage -Name $manifest.Package.Identity.Name -ErrorAction Stop)
    Get-WinoDebugAssessment -ProjectPath $ProjectPath -Name $manifest.Package.Identity.Name `
        -Publisher $manifest.Package.Identity.Publisher -ManifestVersion $manifest.Package.Identity.Version `
        -WinAppVersion $versionText -InstalledPackages $installed
}

function Assert-WinoDebugReady {
    param([Parameter(Mandatory)][string]$ProjectPath, [string]$WinAppVersion)

    $arguments = @{ ProjectPath = $ProjectPath }
    if ($PSBoundParameters.ContainsKey('WinAppVersion')) { $arguments.WinAppVersion = $WinAppVersion }
    $readiness = Get-WinoDebugReadiness @arguments
    if (-not $readiness.Ready) {
        throw "$($readiness.Code): $($readiness.Reason) $($readiness.NextAction)"
    }
    return $readiness
}
