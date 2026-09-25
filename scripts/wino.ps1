<#
.SYNOPSIS
Runs the standard Wino Mail development harness.

.EXAMPLE
.\scripts\wino.ps1 build app

.EXAMPLE
.\scripts\wino.ps1 run playground -NoBuild

.EXAMPLE
.\scripts\wino.ps1 xaml changed -Check

.EXAMPLE
.\scripts\wino.ps1 ui app -Scenario StartupSmoke -Fast
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet("affected", "build", "test", "run", "debug", "doctor", "audit", "ui", "xaml", "help")]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$Target,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Restore,
    [switch]$NoBuild,
    [switch]$Check,
    [switch]$Changed,
    [string[]]$Path,
    [string]$Filter,
    [string[]]$Scenario,
    [string]$Account,
    [string]$ContactDestination,
    [string]$Theme,
    [switch]$UseRunning,
    [switch]$Fast,
    [switch]$List
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($PSBoundParameters.ContainsKey("Configuration") -and $Command -ne "build") {
    throw "-Configuration is supported only by build. Runtime commands use Debug."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$projects = @{
    app = "src\Wino.Mail.WinUI\Wino.Mail.WinUI.csproj"
    controls = "controls\Wino.Mail.Controls\Wino.Mail.Controls.csproj"
    core = "controls\Wino.Mail.Controls.Core\Wino.Mail.Controls.Core.csproj"
    editor = "controls\Wino.Editor\Wino.Editor.csproj"
    playground = "controls\Wino.Mail.Controls.Playground\Wino.Mail.Controls.Playground.csproj"
    viewmodels = "src\Wino.Mail.ViewModels\Wino.Mail.ViewModels.csproj"
}

$testProjects = @{
    controls = "tests\Wino.Mail.Controls.Tests\Wino.Mail.Controls.Tests.csproj"
    core = "tests\Wino.Core.Tests\Wino.Core.Tests.csproj"
    viewmodels = "tests\Wino.Mail.ViewModels.Tests\Wino.Mail.ViewModels.Tests.csproj"
}

function Show-Usage {
    Write-Host @"
Wino Mail development harness

  .\scripts\wino.ps1 affected [-Path <repository-relative path[]>]
  .\scripts\wino.ps1 build <app|controls|core|editor|playground|viewmodels> [-Configuration Debug|Release] [-Restore]
  .\scripts\wino.ps1 test <controls|core|viewmodels> [-Filter <text>] [-NoBuild] [-Restore]
  .\scripts\wino.ps1 run <app|playground> [-NoBuild] [-Restore]
  .\scripts\wino.ps1 debug <app|playground> [-NoBuild] [-Restore]
  .\scripts\wino.ps1 doctor <app|playground>
  .\scripts\wino.ps1 audit app -Account <name> -ContactDestination <name> -Theme <name> [-Scenario <name[]>] [-Restore] [-List]
  .\scripts\wino.ps1 ui app [-Scenario <name>] [-UseRunning] [-NoBuild] [-Fast] [-List]
  .\scripts\wino.ps1 xaml [all|changed] [-Check] [-Path <path[]>]

Builds never deploy or launch. Release is compile-only. Runtime commands use Debug.
The build target 'core' is Wino.Mail.Controls.Core; the test target 'core' is Wino.Core.Tests.
"@
}

function Get-ProjectPath {
    param(
        [Parameter(Mandatory)][hashtable]$Map,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Kind
    )

    if (-not $Map.ContainsKey($Name)) {
        $validNames = @($Map.Keys | Sort-Object) -join ", "
        throw "Unknown $Kind target '$Name'. Valid targets: $validNames."
    }

    return Join-Path $repositoryRoot $Map[$Name]
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "> $Executable $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $Executable @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited with code $LASTEXITCODE."
    }
}

function Get-BuildArguments {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$ProjectName
    )

    $arguments = @("build", $ProjectPath, "-c", $Configuration, "-p:Platform=x64")

    if (-not $Restore) {
        $arguments += "--no-restore"
    }

    if ($ProjectName -ne "core" -and $ProjectName -ne "viewmodels") {
        $arguments += @(
            "/p:RuntimeIdentifier=win-x64",
            "/p:GenerateAppxPackageOnBuild=false",
            "/p:AppxPackageSigningEnabled=false"
        )
    }

    return $arguments
}

function Invoke-WinApp {
    param(
        [Parameter(Mandatory)][string]$ProjectName,
        [switch]$Attached
    )

    if ($ProjectName -notin @("app", "playground")) {
        throw "WinApp can launch only the app or playground target."
    }

    $projectPath = Get-ProjectPath -Map $projects -Name $ProjectName -Kind "run"
    . (Join-Path $PSScriptRoot 'Wino.Debug.ps1')
    Assert-WinoDebugReady -ProjectPath $projectPath | Out-Null
    $arguments = @(
        "run", $projectPath,
        "-c", "Debug",
        "-r", "win-x64",
        "-p", "Platform=x64",
        "-p", "GenerateAppxPackageOnBuild=false",
        "-p", "AppxPackageSigningEnabled=false"
    )

    if (-not $Restore) {
        $arguments += "--no-restore"
    }

    if ($NoBuild) {
        $arguments += "--no-build"
    }

    if ($Attached) {
        $arguments += "--debug-output"
    }
    else {
        $arguments += @("--detach", "--json")
    }

    Invoke-ExternalCommand -Executable "winapp" -Arguments $arguments
}

Set-Location $repositoryRoot

switch ($Command) {
    "help" {
        Show-Usage
    }
    "affected" {
        if ($Path) {
            $changedFiles = @($Path)
        }
        else {
            $changedFiles = @(& git diff HEAD --name-only --diff-filter=ACMR)
            if ($LASTEXITCODE -ne 0) {
                throw "Git changed-file discovery failed."
            }

            $changedFiles += @(& git ls-files --others --exclude-standard)
            if ($LASTEXITCODE -ne 0) {
                throw "Git untracked-file discovery failed."
            }
        }

        $changedFiles = @($changedFiles | Where-Object { $_ } | Sort-Object -Unique)

        if ($changedFiles.Count -eq 0) {
            Write-Host "No changed files were found."
            break
        }

        $changedFiles | & codegraph affected --stdin --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "CodeGraph affected analysis failed."
        }
    }
    "build" {
        if ([string]::IsNullOrWhiteSpace($Target)) {
            throw "A build target is required."
        }

        $projectPath = Get-ProjectPath -Map $projects -Name $Target -Kind "build"
        $arguments = Get-BuildArguments -ProjectPath $projectPath -ProjectName $Target
        Invoke-ExternalCommand -Executable "dotnet" -Arguments $arguments
    }
    "test" {
        if ([string]::IsNullOrWhiteSpace($Target)) {
            throw "A test target is required."
        }

        $projectPath = Get-ProjectPath -Map $testProjects -Name $Target -Kind "test"
        $arguments = @("test", $projectPath, "-c", "Debug", "/p:Platform=x64")

        if ($NoBuild) {
            $arguments += "--no-build"
        }

        if (-not $Restore) {
            $arguments += "--no-restore"
        }

        if (-not [string]::IsNullOrWhiteSpace($Filter)) {
            $arguments += @("--filter", $Filter)
        }

        Invoke-ExternalCommand -Executable "dotnet" -Arguments $arguments
    }
    "run" {
        Invoke-WinApp -ProjectName $Target
    }
    "debug" {
        Invoke-WinApp -ProjectName $Target -Attached
    }
    "doctor" {
        if ($Target -notin @('app', 'playground')) { throw "Doctor requires the app or playground target." }
        . (Join-Path $PSScriptRoot 'Wino.Debug.ps1')
        Get-WinoDebugReadiness -ProjectPath (Get-ProjectPath -Map $projects -Name $Target -Kind 'doctor') | ConvertTo-Json -Depth 6
    }
    "audit" {
        if ($Target -ne 'app') { throw "Audit supports only the app target." }
        if ($NoBuild -or $UseRunning) { throw "Audit builds current Debug source. -NoBuild and -UseRunning are not supported." }
        $arguments = @{}
        if ($List) { $arguments.List = $true }
        if ($Scenario) { $arguments.Scenario = $Scenario }
        if ($Account) { $arguments.Account = $Account }
        if ($ContactDestination) { $arguments.ContactDestination = $ContactDestination }
        if ($Theme) { $arguments.Theme = $Theme }
        if ($Restore) { $arguments.Restore = $true }
        & (Join-Path $PSScriptRoot 'ui-audit/Run-WinoRegression.ps1') @arguments
    }
    "ui" {
        if ($Target -ne "app") {
            throw "The playground UI runner is planned but is not implemented yet. Use target 'app'."
        }

        $arguments = @{}

        if ($Scenario) { $arguments.Scenario = $Scenario }
        if ($UseRunning) { $arguments.UseRunning = $true }
        if ($NoBuild) { $arguments.NoBuild = $true }
        if ($Fast) { $arguments.Fast = $true }
        if ($List) { $arguments.List = $true }

        & (Join-Path $repositoryRoot "tests\ui\Run-WinoUiTests.ps1") @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "The Wino Mail UI runner failed."
        }
    }
    "xaml" {
        $arguments = @{}
        $useChangedFiles = $Changed -or $Target -eq "changed"

        if ($Check) { $arguments.Check = $true }
        if ($useChangedFiles) { $arguments.Changed = $true }
        if ($Path) { $arguments.Path = $Path }

        & (Join-Path $repositoryRoot "scripts\format-xaml.ps1") @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "XAML formatting failed."
        }
    }
}
