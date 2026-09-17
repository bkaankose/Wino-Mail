#requires -Version 7.0
# These checks replace native tools with in-process doubles. They never build or launch Wino.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$harness = Join-Path $PSScriptRoot '../../scripts/wino.ps1'
$global:WinoHarnessTest_passed = 0
$global:WinoHarnessTest_calls = [System.Collections.Generic.List[object]]::new()
$global:WinoHarnessTest_toolExit = 0
$global:WinoHarnessTest_gitFailure = ''
$global:WinoHarnessTest_gitTracked = @('src/tracked.cs')
$global:WinoHarnessTest_gitUntracked = @('src/new.cs', 'src/tracked.cs')
$global:WinoHarnessTest_graphInput = @()
$global:WinoHarnessTest_packages = @()

function dotnet {
    $global:WinoHarnessTest_calls.Add(@{ Tool = 'dotnet'; Arguments = @($args) })
    $global:LASTEXITCODE = $global:WinoHarnessTest_toolExit
}
function winapp {
    if ($args[0] -eq '--version') { $global:LASTEXITCODE = 0; return '0.6.0' }
    $global:WinoHarnessTest_calls.Add(@{ Tool = 'winapp'; Arguments = @($args) })
    $global:LASTEXITCODE = $global:WinoHarnessTest_toolExit
}
function Get-AppxPackage { param($Name, $ErrorAction) $global:WinoHarnessTest_packages }
function git {
    $global:WinoHarnessTest_calls.Add(@{ Tool = 'git'; Arguments = @($args) })
    $global:LASTEXITCODE = if ($args[0] -eq $global:WinoHarnessTest_gitFailure) { 17 } else { 0 }
    if ($args[0] -eq 'diff') { $global:WinoHarnessTest_gitTracked } else { $global:WinoHarnessTest_gitUntracked }
}
function codegraph {
    $global:WinoHarnessTest_graphInput = @($input)
    $global:WinoHarnessTest_calls.Add(@{ Tool = 'codegraph'; Arguments = @($args) })
    $global:LASTEXITCODE = $global:WinoHarnessTest_toolExit
}
function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        Assert-True ($_.Exception.Message -match $Pattern) "Unexpected exception: $($_.Exception.Message)"
        return
    }
    throw "Expected failure: $Pattern"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    $global:WinoHarnessTest_calls.Clear()
    $global:WinoHarnessTest_toolExit = 0
    $global:WinoHarnessTest_gitFailure = ''
    $global:WinoHarnessTest_packages = @()
    & $Action
    $global:WinoHarnessTest_passed++
    Write-Host "PASS $Name"
}

$originalLocation = Get-Location
try {
    Test-Case 'Debug build retains package and restore flags' {
        & $harness build app
        $call = $global:WinoHarnessTest_calls[0]
        Assert-True ($call.Tool -eq 'dotnet') 'Wrong build tool.'
        foreach ($flag in @('Debug', '--no-restore', '-p:Platform=x64', '/p:RuntimeIdentifier=win-x64', '/p:GenerateAppxPackageOnBuild=false', '/p:AppxPackageSigningEnabled=false')) {
            Assert-True ($call.Arguments -contains $flag) "Missing $flag"
        }
    }
    Test-Case 'Release compiles without launch or packaging' {
        & $harness build app -Configuration Release
        Assert-True ($global:WinoHarnessTest_calls.Count -eq 1 -and $global:WinoHarnessTest_calls[0].Tool -eq 'dotnet') 'Unexpected dispatch.'
        $arguments = $global:WinoHarnessTest_calls[0].Arguments
        Assert-True ($arguments[0] -eq 'build' -and $arguments -contains 'Release') 'Release build missing.'
        Assert-True ($arguments -contains '/p:GenerateAppxPackageOnBuild=false') 'Package generation enabled.'
        Assert-True ($arguments -contains '/p:AppxPackageSigningEnabled=false') 'Signing enabled.'
    }
    foreach ($command in @('run', 'debug', 'ui', 'test', 'audit')) {
        Test-Case "Reject Release for $command before tool dispatch" {
            Assert-Throws { & $harness $command app -Configuration Release } 'supported only by build'
            Assert-True ($global:WinoHarnessTest_calls.Count -eq 0) 'Rejected command still dispatched.'
        }
    }
    Test-Case 'Explicit restore removes no-restore' {
        & $harness build app -Restore
        Assert-True ($global:WinoHarnessTest_calls[0].Arguments -notcontains '--no-restore') 'Restore was suppressed.'
    }
    Test-Case 'Test filter stays one argument and reuses current output on request' {
        $filter = 'FullyQualifiedName~MailListStore|DisplayName~name with spaces'
        & $harness test viewmodels -Filter $filter -NoBuild
        $arguments = $global:WinoHarnessTest_calls[0].Arguments
        Assert-True ($arguments -contains $filter) 'Filter argument boundaries changed.'
        Assert-True ($arguments -contains '--no-build' -and $arguments -contains '--no-restore') 'Reuse flags missing.'
    }
    Test-Case 'Run retains Debug project mode and no-build' {
        & $harness run app -NoBuild
        $call = $global:WinoHarnessTest_calls[0]
        Assert-True ($call.Tool -eq 'winapp' -and $call.Arguments -contains 'Debug') 'Wrong runtime command.'
        Assert-True ($call.Arguments -contains '--no-build') 'No-build missing.'
        Assert-True ($call.Arguments[1] -like '*.csproj') 'Project mode missing.'
    }
    Test-Case 'Explicit affected paths bypass unrelated Git changes and deduplicate' {
        & $harness affected -Path @('src/with space.cs', 'src/example.cs', 'src/example.cs')
        Assert-True ($global:WinoHarnessTest_calls.Count -eq 1 -and $global:WinoHarnessTest_calls[0].Tool -eq 'codegraph') 'Unexpected discovery.'
        Assert-True ($global:WinoHarnessTest_graphInput.Count -eq 2 -and $global:WinoHarnessTest_graphInput -contains 'src/with space.cs') 'Paths changed.'
    }
    Test-Case 'Default affected includes tracked and untracked files once' {
        & $harness affected
        Assert-True (($global:WinoHarnessTest_graphInput -join '|') -eq 'src/new.cs|src/tracked.cs') 'Wrong changed-file set.'
    }
    Test-Case 'Clean tree does not invoke CodeGraph' {
        $global:WinoHarnessTest_gitTracked = @()
        $global:WinoHarnessTest_gitUntracked = @()
        & $harness affected
        Assert-True (@($global:WinoHarnessTest_calls | Where-Object Tool -eq 'codegraph').Count -eq 0) 'Unexpected graph query.'
        $global:WinoHarnessTest_gitTracked = @('src/tracked.cs')
        $global:WinoHarnessTest_gitUntracked = @('src/new.cs')
    }
    foreach ($stage in @('diff', 'ls-files')) {
        Test-Case "Git $stage failure stops affected analysis" {
            $global:WinoHarnessTest_gitFailure = $stage
            Assert-Throws { & $harness affected } 'Git .* discovery failed'
            Assert-True (@($global:WinoHarnessTest_calls | Where-Object Tool -eq 'codegraph').Count -eq 0) 'Failed discovery reached CodeGraph.'
        }
    }
    Test-Case 'CodeGraph failure propagates' {
        $global:WinoHarnessTest_toolExit = 9
        Assert-Throws { & $harness affected -Path src/example.cs } 'CodeGraph affected analysis failed'
    }
    Test-Case 'Build failure propagates' {
        $global:WinoHarnessTest_toolExit = 8
        Assert-Throws { & $harness build app } 'dotnet exited with code 8'
    }
    Test-Case 'Unknown build target fails before dispatch' {
        Assert-Throws { & $harness build missing } 'Unknown build target'
        Assert-True ($global:WinoHarnessTest_calls.Count -eq 0) 'Unknown target dispatched.'
    }
    Test-Case 'Signed package blocks run before deployment' {
        $manifest = [xml](Get-Content (Join-Path $PSScriptRoot '../../src/Wino.Mail.WinUI/Package.appxmanifest'))
        $global:WinoHarnessTest_packages = @([pscustomobject]@{
            Name=$manifest.Package.Identity.Name; Publisher=$manifest.Package.Identity.Publisher
            Version='2.1.0.0'; PackageFamilyName='family'; InstallLocation='C:/Program Files/WindowsApps/package'
            IsDevelopmentMode=$false; SignatureKind='Developer'
        })
        Assert-Throws { & $harness run app -NoBuild } 'InstalledPackageConflict'
        Assert-True ($global:WinoHarnessTest_calls.Count -eq 0) 'Blocked package reached deployment.'
    }
    Test-Case 'Audit rejects stale-output shortcuts' {
        Assert-Throws { & $harness audit app -NoBuild } 'not supported'
        Assert-Throws { & $harness audit app -UseRunning } 'not supported'
        Assert-True ($global:WinoHarnessTest_calls.Count -eq 0) 'Rejected audit dispatched.'
    }
    Write-Host "$global:WinoHarnessTest_passed harness checks passed. Native tools were not invoked."
}
finally {
    Set-Location $originalLocation
}

