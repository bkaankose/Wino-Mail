param(
    [ValidateSet("Mail", "Calendar", "Contacts", "Settings")]
    [string]$Mode = "Mail",

    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",

    [string]$Configuration = "Debug",
    [string]$ProjectPath = "src\Wino.Mail.WinUI\Wino.Mail.WinUI.csproj",
    [string]$OutputRoot = "artifacts\winapp-smoke",
    [int]$TimeoutSeconds = 25,
    [switch]$Restore,
    [switch]$Build,
    [switch]$Clean,
    [switch]$KeepRunning,
    [switch]$UseInstalledPackageIdentity
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Get-ProjectValue {
    param(
        [Parameter(Mandatory)]
        [xml]$ProjectXml,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $values = @($ProjectXml.Project.PropertyGroup | ForEach-Object { $_.$Name } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($values.Count -eq 0) {
        return $null
    }

    return $values[0]
}

function Get-FirstJsonObject {
    param([string[]]$Lines)

    $text = ($Lines -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return $text | ConvertFrom-Json
    }
    catch {
        $json = ($Lines | Where-Object { $_ -match "^\s*[\{\[]" } | Select-Object -Last 1)
        if ([string]::IsNullOrWhiteSpace($json)) {
            return $null
        }

        return $json | ConvertFrom-Json
    }
}

function Wait-WinoWindow {
    param(
        [Parameter(Mandatory)]
        [int]$AppPid,

        [Parameter(Mandatory)]
        [int]$Timeout
    )

    $deadline = (Get-Date).AddSeconds($Timeout)

    do {
        $windowOutput = & winapp ui list-windows -a $AppPid --json 2>$null
        if ($LASTEXITCODE -eq 0 -and $windowOutput) {
            try {
                $windows = @($windowOutput | ConvertFrom-Json)
                $mainWindow = $windows |
                    Where-Object { $_.title -and $_.title -ne "PopupHost" -and $_.width -gt 0 -and $_.height -gt 0 } |
                    Select-Object -First 1

                if ($mainWindow) {
                    return $mainWindow
                }
            }
            catch {
                Write-Verbose "Window JSON was not ready yet: $_"
            }
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for a visible Wino window from PID $AppPid."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectFullPath = (Resolve-Path (Join-Path $repoRoot $ProjectPath)).Path
$projectDirectory = Split-Path -Parent $projectFullPath
$projectXml = [xml](Get-Content -Path $projectFullPath -Raw)
$targetFramework = Get-ProjectValue -ProjectXml $projectXml -Name "TargetFramework"

if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not read TargetFramework from $projectFullPath."
}

$runtimeIdentifier = switch ($Platform) {
    "x86" { "win-x86" }
    "ARM64" { "win-arm64" }
    default { "win-x64" }
}

$assetsFile = Join-Path $projectDirectory "obj\project.assets.json"
if ($Build) {
    if ($Restore -or -not (Test-Path $assetsFile)) {
        Invoke-Checked "dotnet" @(
            "restore",
            $projectFullPath,
            "--configfile",
            (Join-Path $repoRoot "nuget.config"),
            "-p:Platform=$Platform",
            "-p:RuntimeIdentifier=$runtimeIdentifier"
        )
    }

    Invoke-Checked "dotnet" @(
        "build",
        $projectFullPath,
        "-c",
        $Configuration,
        "--no-restore",
        "/p:Platform=$Platform",
        "/p:RuntimeIdentifier=$runtimeIdentifier",
        "/p:GenerateAppxPackageOnBuild=false",
        "/p:AppxPackageSigningEnabled=false"
    )
}

$outputDirectory = Join-Path $projectDirectory "bin\$Platform\$Configuration\$targetFramework\$runtimeIdentifier"
if (-not (Test-Path $outputDirectory)) {
    throw "Build output was not found at $outputDirectory."
}

$appxManifest = Join-Path $outputDirectory "AppxManifest.xml"
$exePath = Join-Path $outputDirectory "Wino.Mail.WinUI.exe"

# NativeAOT Release builds place the runnable package layout under AppX while
# framework-dependent builds keep it at the target-framework output root.
if ((-not (Test-Path $appxManifest) -or -not (Test-Path $exePath)) -and
    (Test-Path (Join-Path $outputDirectory "AppX\AppxManifest.xml")) -and
    (Test-Path (Join-Path $outputDirectory "AppX\Wino.Mail.WinUI.exe"))) {
    $outputDirectory = Join-Path $outputDirectory "AppX"
    $appxManifest = Join-Path $outputDirectory "AppxManifest.xml"
    $exePath = Join-Path $outputDirectory "Wino.Mail.WinUI.exe"
}

if (-not (Test-Path $appxManifest)) {
    throw "AppxManifest.xml was not found at $appxManifest."
}

if (-not (Test-Path $exePath)) {
    throw "Wino.Mail.WinUI.exe was not found at $exePath."
}

$runDirectory = Join-Path $repoRoot $OutputRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactDirectory = Join-Path $runDirectory $timestamp
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

# A development registration with the Store identity can replace the installed
# Release package registration. Keep Debug smoke runs side-by-side by default.
$originalAppxManifestContent = $null
if ($Configuration -eq "Debug" -and -not $UseInstalledPackageIdentity) {
    $originalAppxManifestContent = Get-Content -LiteralPath $appxManifest -Raw
    $isolatedManifest = [xml]$originalAppxManifestContent
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($isolatedManifest.NameTable)
    $namespaceManager.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespaceManager.AddNamespace("mp", "http://schemas.microsoft.com/appx/2014/phone/manifest")
    $namespaceManager.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")

    $identity = $isolatedManifest.SelectSingleNode("/f:Package/f:Identity", $namespaceManager)
    $identity.SetAttribute("Name", "58272BurakKSE.WinoMailPreview.Debug")

    $phoneIdentity = $isolatedManifest.SelectSingleNode("/f:Package/mp:PhoneIdentity", $namespaceManager)
    if ($phoneIdentity) {
        $phoneIdentity.SetAttribute("PhoneProductId", "e4d0792b-5187-4ef4-b552-b4b873908e78")
    }

    $displayName = $isolatedManifest.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", $namespaceManager)
    if ($displayName) {
        $displayName.InnerText = "Wino Mail (Debug)"
    }

    foreach ($visualElements in $isolatedManifest.SelectNodes("/f:Package/f:Applications/f:Application/uap:VisualElements", $namespaceManager)) {
        $visualElements.SetAttribute("DisplayName", "$($visualElements.GetAttribute('DisplayName')) (Debug)")
    }

    # COM class IDs are machine-wide registrations and must not overlap Release.
    foreach ($attribute in $isolatedManifest.SelectNodes("//@ToastActivatorCLSID | //@Id", $namespaceManager)) {
        if ($attribute.Value -eq "72c6d2d0-2538-44fe-a1b1-499f47bb1181") {
            $attribute.Value = "64cc773f-6f99-4d74-bb16-7d1f7b0366a1"
        }
    }

    # Development-mode registration requires the manifest to be named
    # AppxManifest.xml at the package root. Restore the generated Release
    # manifest immediately after WinApp CLI finishes registering the package.
    $isolatedManifest.Save($appxManifest)
}

$launchArgument = switch ($Mode) {
    "Calendar" { "--wino-calendar" }
    "Contacts" { "--mode=contacts" }
    "Settings" { "--mode=settings" }
    default { "--wino-mail" }
}

$runArguments = @(
    "run",
    $outputDirectory,
    "--manifest",
    $appxManifest,
    "--output-appx-directory",
    (Join-Path $artifactDirectory "AppX"),
    "--exe",
    "Wino.Mail.WinUI.exe",
    "--detach",
    "--json",
    "--args",
    $launchArgument
)

if ($Clean) {
    $runArguments += "--clean"
}

Write-Host "> winapp $($runArguments -join ' ')"
$runOutput = $null
try {
    $runOutput = & winapp @runArguments 2>&1
}
finally {
    if ($null -ne $originalAppxManifestContent) {
        Set-Content -LiteralPath $appxManifest -Value $originalAppxManifestContent -NoNewline
    }
}
$runOutput | Set-Content -Path (Join-Path $artifactDirectory "winapp-run.log")

if ($LASTEXITCODE -ne 0) {
    $runOutput | ForEach-Object { Write-Host $_ }
    throw "winapp run exited with code $LASTEXITCODE."
}

$launch = Get-FirstJsonObject -Lines $runOutput
$appPid = $null
if ($launch) {
    $appPid = $launch.pid
    if (-not $appPid) {
        $appPid = $launch.processId
    }
}

if (-not $appPid) {
    $pidLine = ($runOutput | Select-String -Pattern "PID[:\s]+(?<pid>\d+)" | Select-Object -First 1)
    if ($pidLine) {
        $appPid = [int]$pidLine.Matches[0].Groups["pid"].Value
    }
}

if (-not $appPid) {
    $runOutput | ForEach-Object { Write-Host $_ }
    throw "Could not find launched process id in winapp output."
}

$mainWindow = Wait-WinoWindow -AppPid $appPid -Timeout $TimeoutSeconds
$inspectPath = Join-Path $artifactDirectory "inspect-interactive.json"
$screenshotPath = Join-Path $artifactDirectory "window.png"

& winapp ui inspect -a $appPid --interactive --json --depth 8 2>$null | Set-Content -Path $inspectPath
& winapp ui screenshot -a $appPid --json -o $screenshotPath 2>$null | Set-Content -Path (Join-Path $artifactDirectory "screenshot.json")

$result = [PSCustomObject]@{
    status = "PASS"
    mode = $Mode
    pid = [int]$appPid
    window = $mainWindow
    outputDirectory = $outputDirectory
    artifactDirectory = $artifactDirectory
    inspect = $inspectPath
    screenshot = $screenshotPath
    keptRunning = [bool]$KeepRunning
}

$resultPath = Join-Path $artifactDirectory "result.json"
$result | ConvertTo-Json -Depth 6 | Set-Content -Path $resultPath

Write-Host ""
Write-Host "Wino winapp smoke PASS"
Write-Host "PID: $appPid"
Write-Host "Window: $($mainWindow.title)"
Write-Host "Artifacts: $artifactDirectory"

if (-not $KeepRunning) {
    Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped PID $appPid. Re-run with -KeepRunning for interactive UIA testing."
}
