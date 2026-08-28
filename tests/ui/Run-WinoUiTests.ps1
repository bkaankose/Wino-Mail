[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$Fast,
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$projectPath = Join-Path $repositoryRoot "src\Wino.Mail.WinUI\Wino.Mail.WinUI.csproj"
$manifestPath = Join-Path $repositoryRoot "src\Wino.Mail.WinUI\Package.appxmanifest"
$testsPath = Join-Path $PSScriptRoot "Tests"
$runTimestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactsPath = Join-Path $repositoryRoot "artifacts\ui-tests\$runTimestamp"
$results = [System.Collections.Generic.List[object]]::new()
$exitCode = 0

function Write-Section {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Get-WinAppVersion {
    $versionText = (& winapp --version).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "WinApp CLI is not available or returned an invalid version."
    }

    try {
        return [version]$versionText
    }
    catch {
        throw "WinApp CLI returned an invalid version: '$versionText'."
    }
}

function Confirm-PackageIdentity {
    $manifest = [xml](Get-Content -Raw $manifestPath)
    $identity = $manifest.Package.Identity
    $installedPackage = Get-AppxPackage -Name $identity.Name | Select-Object -First 1

    if ($null -eq $installedPackage) {
        Write-Host "No existing package registration was found. WinApp will register the checked-in Debug identity."
        return
    }

    if ($installedPackage.Publisher -ne $identity.Publisher) {
        throw "Installed package publisher '$($installedPackage.Publisher)' does not match manifest publisher '$($identity.Publisher)'."
    }

    Write-Host "Package identity verified: $($installedPackage.PackageFamilyName)"
}

function Start-WinoDebugApp {
    $arguments = @(
        "run",
        $projectPath,
        "-c", "Debug",
        "-r", "win-x64",
        "--no-restore",
        "-p", "Platform=x64",
        "-p", "GenerateAppxPackageOnBuild=false",
        "-p", "AppxPackageSigningEnabled=false",
        "--detach",
        "--json"
    )

    if ($NoBuild) {
        $arguments += "--no-build"
    }

    $launchOutput = & winapp @arguments
    $launchText = $launchOutput -join [Environment]::NewLine
    $launchResult = $null

    try {
        $jsonStart = $launchText.LastIndexOf("{")

        if ($jsonStart -ge 0) {
            $launchResult = $launchText.Substring($jsonStart) | ConvertFrom-Json
        }
    }
    catch {
        $launchResult = $null
    }

    if ($LASTEXITCODE -ne 0) {
        $errorProperty = if ($null -ne $launchResult) {
            $launchResult.PSObject.Properties["Error"]
        }

        $failureDetail = if ($null -ne $errorProperty) {
            $errorProperty.Value
        }
        else {
            $launchText
        }

        throw "WinApp failed to build or launch Wino Mail: $failureDetail"
    }

    if ($null -eq $launchResult) {
        throw "WinApp returned an unexpected launch result: $launchText"
    }

    $appProcessId = [int]$launchResult.ProcessId

    if ($appProcessId -le 0) {
        throw "WinApp did not return a valid process ID."
    }

    return $appProcessId
}

function Get-WinoMainWindow {
    $windowOutput = & winapp ui list-windows -a "Wino.Mail.WinUI" --json 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $windowOutput) {
        return $null
    }

    try {
        $parsedWindows = ($windowOutput -join [Environment]::NewLine) | ConvertFrom-Json
        $windows = @($parsedWindows)
        return $windows | Where-Object { $_.title -ne "PopupHost" } | Select-Object -First 1
    }
    catch {
        return $null
    }
}

function Wait-ForWinoWindow {
    $deadline = (Get-Date).AddSeconds(20)

    do {
        # Query by app name because Wino is single-instance. A fresh activation can
        # redirect into an existing process and make the PID returned by winapp exit.
        $mainWindow = Get-WinoMainWindow

        if ($null -ne $mainWindow) {
            return $mainWindow
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    throw "Wino Mail did not expose a UI window within 20 seconds."
}

try {
    Set-Location $repositoryRoot

    Write-Section "Checking prerequisites"
    $winAppVersion = Get-WinAppVersion

    if ($winAppVersion -lt [version]"0.6.0") {
        throw "WinApp CLI 0.6.0 or later is required. Installed version: $winAppVersion"
    }

    Write-Host "WinApp CLI $winAppVersion"
    Confirm-PackageIdentity

    New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null

    $mainWindow = Get-WinoMainWindow

    if ($null -eq $mainWindow) {
        Write-Section "Building and launching Wino Mail"
        Start-WinoDebugApp | Out-Null
        $mainWindow = Wait-ForWinoWindow
    }
    else {
        Write-Section "Using the running Wino Mail instance"
        Write-Host "Close Wino Mail before running this script when you want it rebuilt first."
    }

    $appPid = [int]$mainWindow.processId
    Write-Host "Wino Mail PID: $appPid"
    Write-Host "Window: $($mainWindow.title) [$($mainWindow.hwnd)]"

    # WinApp refuses mouse input to a background window. Set UIA focus once so
    # every test starts with the existing Wino window in the foreground.
    $global:LASTEXITCODE = 0
    & winapp ui focus "NavigationView" -a $appPid *> $null

    $testFiles = @(Get-ChildItem -Path $testsPath -Filter "*.UiTest.ps1" -File | Sort-Object Name)

    if ($testFiles.Count -eq 0) {
        throw "No UI tests were found in '$testsPath'."
    }

    $stepDelayMilliseconds = if ($Fast) { 0 } else { 700 }
    Write-Section "Running $($testFiles.Count) UI test(s)"

    foreach ($testFile in $testFiles) {
        $startedAt = Get-Date

        try {
            & $testFile.FullName `
                -AppPid $appPid `
                -ArtifactsPath $artifactsPath `
                -StepDelayMilliseconds $stepDelayMilliseconds

            $results.Add([pscustomobject]@{
                    Test = $testFile.BaseName
                    Status = "PASS"
                    DurationMilliseconds = [int]((Get-Date) - $startedAt).TotalMilliseconds
                    Error = $null
                })

            Write-Host "PASS: $($testFile.BaseName)" -ForegroundColor Green
        }
        catch {
            $results.Add([pscustomobject]@{
                    Test = $testFile.BaseName
                    Status = "FAIL"
                    DurationMilliseconds = [int]((Get-Date) - $startedAt).TotalMilliseconds
                    Error = $_.Exception.Message
                })

            Write-Host "FAIL: $($testFile.BaseName)" -ForegroundColor Red
            Write-Host $_.Exception.Message -ForegroundColor Red
            $exitCode = 1
        }
    }

    $resultsPath = Join-Path $artifactsPath "test-results.json"
    $results | ConvertTo-Json | Set-Content -Path $resultsPath -Encoding utf8

    Write-Section "Results"
    $results | Format-Table Test, Status, DurationMilliseconds -AutoSize
    Write-Host "Artifacts: $artifactsPath"
    Write-Host "Wino Mail remains open so you can inspect its final state."
}
catch {
    Write-Host ""
    Write-Host "UI test runner failed: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Set-Location $repositoryRoot

    if (-not $NoPause) {
        Write-Host ""
        Read-Host "Press Enter to close this test window" | Out-Null
    }
}

exit $exitCode
