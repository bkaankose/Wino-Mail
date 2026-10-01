#requires -Version 7.2
# Filesystem and command-selection tests only. No application or UI automation runs here.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/development/start-wino.ps1')

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('wino-f5-tests-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $fixture 'src/Wino.Mail.WinUI'
$output = Join-Path $app 'bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64'
$null = New-Item -ItemType Directory -Path $app, "$fixture/scripts/development", "$output/AppX" -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../scripts/development/start-wino.ps1') -Destination "$fixture/scripts/development/start-wino.ps1"
'<Project><PropertyGroup><TargetFramework>net10.0-windows10.0.26100.0</TargetFramework></PropertyGroup></Project>' | Set-Content "$app/Wino.Mail.WinUI.csproj"
'<Package><Identity Name="Test.App" Publisher="CN=Test" /></Package>' | Set-Content "$app/Package.appxmanifest"
'initial source' | Set-Content "$app/App.cs"
$script:calls = @()
$script:fail = $false
$script:editDuringRun = $false
$script:wrongPublisher = $false
$script:toolVersion = 'test-1'
$script:stopped = @()
$script:passed = 0

function dotnet { $global:LASTEXITCODE = 0; $script:toolVersion }
function winapp {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--version') { return 'test-winapp' }
    $script:calls += ($args -contains '--no-build')
    if ($script:fail) { $global:LASTEXITCODE = 1; return }
    foreach ($name in @('Wino.Mail.WinUI.exe', 'Wino.Mail.WinUI.dll', 'Wino.Mail.WinUI.pdb', 'AppX/AppxManifest.xml', 'AppX/Wino.Mail.WinUI.exe', 'dependency.dll')) {
        if ($args -notcontains '--no-build') { 'build output' | Set-Content -LiteralPath (Join-Path $output $name) }
    }
    if ($script:editDuringRun) { 'concurrent edit' | Set-Content "$app/App.cs" }
}
function Get-AppxPackage {
    param($Name)
    [pscustomobject]@{ Name = 'Test.App'; Publisher = $(if ($script:wrongPublisher) { 'CN=Other' } else { 'CN=Test' }); IsDevelopmentMode = $true; InstallLocation = "$output/AppX" }
}
function Get-Process {
    [pscustomobject]@{ Id = 1; Path = "$output/AppX\Wino.Mail.WinUI.exe" }
    [pscustomobject]@{ Id = 2; Path = 'C:\Other\Wino.Mail.WinUI.exe' }
}
function Stop-Process {
    param([Parameter(ValueFromPipeline)]$InputObject, [switch]$Force)
    process { $script:stopped += $InputObject.Id }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Run-Case([string]$Name, [bool]$ExpectReuse) {
    Start-Wino $fixture
    Assert ($script:calls[-1] -eq $ExpectReuse) $Name
    $script:passed++
    Write-Host "PASS $Name"
}

try {
    Run-Case 'First run builds' $false
    Run-Case 'Unchanged input skips the build' $true
    $timestamp = (Get-Item "$app/App.cs").LastWriteTimeUtc
    'changed source' | Set-Content "$app/App.cs"
    (Get-Item "$app/App.cs").LastWriteTimeUtc = $timestamp
    Run-Case 'Content changes with preserved timestamps build' $false
    'new source' | Set-Content "$app/New.cs"
    Run-Case 'Added source builds' $false
    [IO.File]::Delete("$app/New.cs")
    Run-Case 'Deleted source builds' $false
    '<Page />' | Set-Content "$app/View.xaml"
    Run-Case 'XAML changes build' $false
    'asset' | Set-Content "$app/icon.png"
    Run-Case 'Asset changes build' $false
    '<Project />' | Set-Content "$fixture/Directory.Packages.props"
    Run-Case 'Dependency settings changes build' $false
    $script:toolVersion = 'test-2'
    Run-Case 'SDK changes build' $false
    [IO.File]::Delete("$output/dependency.dll")
    Run-Case 'Missing dependency output builds' $false
    'external output edit' | Set-Content "$output/Wino.Mail.WinUI.dll"
    Run-Case 'Externally changed output builds' $false
    '{invalid' | Set-Content "$fixture/artifacts/f5/last-success.json"
    Run-Case 'Corrupt state builds' $false
    Start-Wino $fixture -ForceBuild
    Assert (-not $script:calls[-1]) 'ForceBuild must build'
    $script:passed++
    $script:fail = $true
    try { Start-Wino $fixture; throw 'Expected WinApp failure' } catch { Assert ($_.Exception.Message -match 'WinApp failed') 'Wrong failure' }
    $script:fail = $false
    Run-Case 'Failed run invalidates state' $false
    $script:editDuringRun = $true
    Start-Wino $fixture -ForceBuild
    $script:editDuringRun = $false
    Run-Case 'Edits during build require another build' $false
    $script:wrongPublisher = $true
    $before = $script:calls.Count
    try { Start-Wino $fixture; throw 'Expected identity failure' } catch { Assert ($_.Exception.Message -match 'installed package') 'Wrong identity failure' }
    Assert ($script:calls.Count -eq $before) 'Identity mismatch must stop before deployment'
    Assert (2 -notin $script:stopped) 'Unrelated process must remain running'
    Assert (1 -in $script:stopped) 'Debug package process must stop'
    $script:passed++
    Write-Host "Passed $script:passed checks."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'wino-f5-tests-*') {
        throw 'Unexpected fixture cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
