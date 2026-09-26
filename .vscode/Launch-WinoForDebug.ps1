[CmdletBinding()]
param(
    [switch]$StopOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProjectPath = Join-Path $repositoryRoot "src\Wino.Mail.WinUI\Wino.Mail.WinUI.csproj"
$appOutputDirectory = Join-Path $repositoryRoot "src\Wino.Mail.WinUI\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
$appLayoutDirectory = Join-Path $appOutputDirectory "AppX"
$appExecutablePath = Join-Path $appLayoutDirectory "Wino.Mail.WinUI.exe"
$statePath = Join-Path $repositoryRoot "src\Wino.Mail.WinUI\obj\.vscode-debug-x64.state.json"

$fastProjects = @(
    [pscustomobject]@{ Root = "src\Wino.Core.Domain"; Project = "src\Wino.Core.Domain\Wino.Core.Domain.csproj"; Framework = "net10.0"; Assembly = "Wino.Core.Domain"; Runtime = $true; Order = 0 },
    [pscustomobject]@{ Root = "src\Wino.Messages"; Project = "src\Wino.Messages\Wino.Messaging.csproj"; Framework = "net10.0"; Assembly = "Wino.Messaging"; Runtime = $true; Order = 1 },
    [pscustomobject]@{ Root = "src\Wino.Authentication"; Project = "src\Wino.Authentication\Wino.Authentication.csproj"; Framework = "net10.0"; Assembly = "Wino.Authentication"; Runtime = $true; Order = 2 },
    [pscustomobject]@{ Root = "src\Wino.Services"; Project = "src\Wino.Services\Wino.Services.csproj"; Framework = "net10.0"; Assembly = "Wino.Services"; Runtime = $true; Order = 2 },
    [pscustomobject]@{ Root = "src\Wino.Core"; Project = "src\Wino.Core\Wino.Core.csproj"; Framework = "net10.0"; Assembly = "Wino.Core"; Runtime = $true; Order = 3 },
    [pscustomobject]@{ Root = "src\Wino.Core.ViewModels"; Project = "src\Wino.Core.ViewModels\Wino.Core.ViewModels.csproj"; Framework = "net10.0"; Assembly = "Wino.Core.ViewModels"; Runtime = $true; Order = 4 },
    [pscustomobject]@{ Root = "controls\Wino.Mail.Controls.Core"; Project = "controls\Wino.Mail.Controls.Core\Wino.Mail.Controls.Core.csproj"; Framework = "net10.0-windows10.0.26100.0"; Assembly = "Wino.Mail.Controls.Core"; Runtime = $false; Order = 4 },
    [pscustomobject]@{ Root = "src\Wino.Mail.ViewModels"; Project = "src\Wino.Mail.ViewModels\Wino.Mail.ViewModels.csproj"; Framework = "net10.0-windows10.0.26100.0"; Assembly = "Wino.Mail.ViewModels"; Runtime = $true; Order = 5 },
    [pscustomobject]@{ Root = "src\Wino.Calendar.ViewModels"; Project = "src\Wino.Calendar.ViewModels\Wino.Calendar.ViewModels.csproj"; Framework = "net10.0-windows10.0.26100.0"; Assembly = "Wino.Calendar.ViewModels"; Runtime = $true; Order = 5 },
    [pscustomobject]@{ Root = "src\Wino.NotificationHost.Contracts"; Project = "src\Wino.NotificationHost.Contracts\Wino.NotificationHost.Contracts.csproj"; Framework = "net10.0"; Assembly = "Wino.NotificationHost.Contracts"; Runtime = $true; Order = 5 }
)

# Stops every process that runs from the Debug layout: the app, notification hosts, and the tray companion.
# They all hold files that the build and the WinApp layout sync replace.
function Stop-Debuggee {
    $layoutPrefix = $appLayoutDirectory.TrimEnd('\') + '\'
    $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq "Wino.Mail.WinUI" -or
        ($null -ne $_.Path -and $_.Path.StartsWith($layoutPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    })

    if ($processes.Count -eq 0) {
        return
    }

    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    $processes | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 750
}

if ($StopOnly) {
    Stop-Debuggee
    exit 0
}

function Get-ProjectOutputDirectory {
    param([Parameter(Mandatory)]$Definition)

    $path = Join-Path $repositoryRoot "$($Definition.Root)\bin\x64\Debug\$($Definition.Framework)"

    if ($Definition.Runtime) {
        $path = Join-Path $path "win-x64"
    }

    return $path
}

function Get-ProjectReferenceAssemblyPath {
    param([Parameter(Mandatory)]$Definition)

    $path = Join-Path $repositoryRoot "$($Definition.Root)\obj\x64\Debug\$($Definition.Framework)"

    if ($Definition.Runtime) {
        $path = Join-Path $path "win-x64"
    }

    return Join-Path $path "ref\$($Definition.Assembly).dll"
}

function Get-FileHashOrEmpty {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-BuildInputs {
    $rootInputs = @(
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "nuget.config",
        "WinoMail.slnx"
    )
    $inputScopes = @("src", "controls") + $rootInputs
    $head = & git -C $repositoryRoot rev-parse HEAD 2>$null

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the Git revision used for the F5 build fingerprint."
    }

    $statusLines = @(& git -C $repositoryRoot -c core.quotepath=false status --porcelain=v1 --untracked-files=all --no-renames -- @inputScopes)

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the Git changes used for the F5 build fingerprint."
    }

    $inputs = [ordered]@{ "__git_head__" = "$head" }

    foreach ($line in $statusLines) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) {
            continue
        }

        $status = $line.Substring(0, 2)
        $relativePath = $line.Substring(3).Replace('/', '\')

        if ($relativePath -match '(^|[\\])(bin|obj)[\\]') {
            continue
        }

        $fullPath = Join-Path $repositoryRoot $relativePath
        $inputs[$relativePath] = "$status|$(Get-FileHashOrEmpty $fullPath)"
    }

    return $inputs
}

function Read-BuildState {
    if (-not (Test-Path -LiteralPath $statePath)) {
        return $null
    }

    try {
        $state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json -AsHashtable

        if ($state.Version -ne 3 -or -not $state.Contains("Inputs") -or -not $state.Contains("ReferenceHashes")) {
            return $null
        }

        return $state
    }
    catch {
        return $null
    }
}

function Write-BuildState {
    $referenceHashes = [ordered]@{}

    foreach ($definition in $fastProjects) {
        $referenceHashes[$definition.Assembly] = Get-FileHashOrEmpty (Get-ProjectReferenceAssemblyPath $definition)
    }

    $state = [ordered]@{
        Version = 3
        Inputs = Get-BuildInputs
        ReferenceHashes = $referenceHashes
    }

    $stateDirectory = Split-Path -Parent $statePath
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    Set-Content -LiteralPath $statePath -Value ($state | ConvertTo-Json -Depth 5) -NoNewline
}

function Get-ChangedPaths {
    param(
        [Parameter(Mandatory)]$PreviousInputs,
        [Parameter(Mandatory)]$CurrentInputs
    )

    $allPaths = @($PreviousInputs.Keys) + @($CurrentInputs.Keys) | Sort-Object -Unique

    return @($allPaths | Where-Object {
        -not $PreviousInputs.Contains($_) -or
        -not $CurrentInputs.Contains($_) -or
        $PreviousInputs[$_] -ne $CurrentInputs[$_]
    })
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $Executable @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$Executable exited with code $LASTEXITCODE."
    }
}

function Build-FastProject {
    param([Parameter(Mandatory)]$Definition)

    Write-Host "Building only $($Definition.Assembly)." -ForegroundColor DarkGray
    $arguments = @(
        "build",
        (Join-Path $repositoryRoot $Definition.Project),
        "-c", "Debug",
        "-f", $Definition.Framework,
        "-p:Platform=x64",
        "--no-restore",
        "-v:minimal"
    )

    if ($Definition.Runtime) {
        $arguments += @("-r", "win-x64")
    }

    Invoke-CheckedCommand -Executable "dotnet" -Arguments $arguments
}

function Stage-FastProject {
    param([Parameter(Mandatory)]$Definition)

    $outputDirectory = Get-ProjectOutputDirectory $Definition
    $sourceDll = Join-Path $outputDirectory "$($Definition.Assembly).dll"
    $sourcePdb = Join-Path $outputDirectory "$($Definition.Assembly).pdb"

    if (-not (Test-Path -LiteralPath $sourceDll)) {
        throw "The targeted build did not produce '$sourceDll'."
    }

    # WinApp syncs the AppX layout from each project's own output through the appxrecipe.
    # The app output copy keeps the DLL and its PDB together for the debugger's symbol search.
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $appOutputDirectory "$($Definition.Assembly).dll") -Force

    if (Test-Path -LiteralPath $sourcePdb) {
        Copy-Item -LiteralPath $sourcePdb -Destination (Join-Path $appOutputDirectory "$($Definition.Assembly).pdb") -Force
    }
}

function Test-NotificationHostOutputs {
    $hostExecutables = @(
        "src\Wino.Mail.NotificationHost\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Wino.Mail.NotificationHost.exe",
        "src\Wino.Calendar.NotificationHost\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Wino.Calendar.NotificationHost.exe",
        "src\Wino.People.NotificationHost\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Wino.People.NotificationHost.exe",
        "src\Wino.Tasks.NotificationHost\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Wino.Tasks.NotificationHost.exe"
    )

    return @($hostExecutables | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repositoryRoot $_)) }).Count -eq 0
}

function Build-App {
    param(
        [switch]$Restore,
        [switch]$SkipProjectReferences,
        [switch]$SkipNotificationHosts
    )

    $arguments = @(
        "build", $appProjectPath,
        "-c", "Debug",
        "-r", "win-x64",
        "-p:Platform=x64",
        "-p:GenerateAppxPackageOnBuild=false",
        "-p:AppxPackageSigningEnabled=false",
        "-v:minimal"
    )

    if ($SkipProjectReferences) {
        $arguments += "-p:BuildProjectReferences=false"
    }

    if ($SkipNotificationHosts) {
        $arguments += "-p:WinoSkipNotificationHostBuild=true"
    }

    if ($Restore) {
        Invoke-CheckedCommand -Executable "dotnet" -Arguments $arguments
        return
    }

    & dotnet @($arguments + "--no-restore") | Tee-Object -Variable buildOutput | Out-Host
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        return
    }

    # A test run or a branch switch can leave restore assets without the win-x64 target. Retry only that case with restore.
    if (($buildOutput -join "`n") -notmatch "NETSDK1004|NETSDK1047|project\.assets\.json") {
        throw "dotnet exited with code $exitCode."
    }

    Write-Host "Restore assets are missing or stale. Retrying the build with restore." -ForegroundColor Yellow
    Invoke-CheckedCommand -Executable "dotnet" -Arguments $arguments
}

# WinApp 0.6+ syncs the registered AppX layout from the appxrecipe and keeps the registration,
# so the package is never removed here. That preserves application data and capability consent.
function Invoke-WinApp {
    $arguments = @(
        "run", $appProjectPath,
        "-c", "Debug",
        "-r", "win-x64",
        "-p", "Platform=x64",
        "-p", "GenerateAppxPackageOnBuild=false",
        "-p", "AppxPackageSigningEnabled=false",
        "--no-build",
        "--no-restore",
        "--detach"
    )

    Invoke-CheckedCommand -Executable "winapp" -Arguments $arguments
}

# Stop before any build when the installed package identity does not match the checked-in manifest.
. (Join-Path $repositoryRoot "scripts\Wino.Debug.ps1")
Assert-WinoDebugReady -ProjectPath $appProjectPath | Out-Null

$currentInputs = Get-BuildInputs
$previousState = Read-BuildState
$changedPaths = @(if ($null -eq $previousState) {
    @($currentInputs.Keys)
}
else {
    Get-ChangedPaths -PreviousInputs $previousState.Inputs -CurrentInputs $currentInputs
})

Stop-Debuggee

if ($null -ne $previousState -and $changedPaths.Count -eq 0 -and (Test-Path -LiteralPath $appExecutablePath)) {
    Write-Host "Build inputs are unchanged. Launching existing Debug output." -ForegroundColor DarkGray
    Invoke-WinApp
    exit 0
}

$changedFastProjects = @()
$onlyFastProjectCodeChanged = $null -ne $previousState -and $changedPaths.Count -gt 0

foreach ($path in $changedPaths) {
    if ([System.IO.Path]::GetExtension($path) -ine ".cs") {
        $onlyFastProjectCodeChanged = $false
        break
    }

    $definition = $fastProjects | Where-Object {
        $path.StartsWith("$($_.Root)\", [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1

    if ($null -eq $definition) {
        $onlyFastProjectCodeChanged = $false
        break
    }

    $changedFastProjects += $definition
}

if ($onlyFastProjectCodeChanged) {
    $changedFastProjects = @($changedFastProjects | Sort-Object Order, Assembly -Unique)
    $canStageWithoutAppBuild = $true

    foreach ($definition in $changedFastProjects) {
        Build-FastProject $definition
        $newReferenceHash = Get-FileHashOrEmpty (Get-ProjectReferenceAssemblyPath $definition)
        $oldReferenceHash = if ($previousState.ReferenceHashes.Contains($definition.Assembly)) {
            $previousState.ReferenceHashes[$definition.Assembly]
        }
        else {
            ""
        }

        if ([string]::IsNullOrWhiteSpace($oldReferenceHash) -or $newReferenceHash -ne $oldReferenceHash) {
            $canStageWithoutAppBuild = $false
        }
    }

    if ($canStageWithoutAppBuild) {
        foreach ($definition in $changedFastProjects) {
            Stage-FastProject $definition
        }

        Write-Host "Public contracts are unchanged. Skipping the WinUI app build." -ForegroundColor DarkGray
        Invoke-WinApp
        Write-BuildState
        exit 0
    }

    Write-Host "A public contract changed. Falling back to the full app build." -ForegroundColor DarkGray
}

$appOnlyChange = $null -ne $previousState -and $changedPaths.Count -gt 0 -and @(
    $changedPaths | Where-Object {
        -not $_.StartsWith("src\Wino.Mail.WinUI\", [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetExtension($_) -in @(".csproj", ".props", ".targets", ".manifest")
    }
).Count -eq 0

if ($appOnlyChange -and (Test-NotificationHostOutputs)) {
    Write-Host "Only WinUI app files changed. Skipping dependency and notification-host builds." -ForegroundColor DarkGray
    Build-App -SkipProjectReferences -SkipNotificationHosts
}
else {
    Write-Host "Running the full Debug x64 build." -ForegroundColor DarkGray
    $restoreRequired = $null -eq $previousState -or @($changedPaths | Where-Object {
        $_ -in @("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config", "WinoMail.slnx") -or
        [System.IO.Path]::GetExtension($_) -in @(".csproj", ".props", ".targets")
    }).Count -gt 0

    Build-App -Restore:$restoreRequired
}

Invoke-WinApp
Write-BuildState
