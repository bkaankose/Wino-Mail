#requires -Version 7.0
# This private transport uses raw arguments: advanced PowerShell parameter binding
# can consume application switches, and native -File does not support a -- binder delimiter.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($args.Count -lt 4 -or $args[3] -ne '--') {
    throw 'Run bridge expects: project path, runtime identifier, platform, --, application arguments.'
}
$ProjectPath = $args[0]
$RuntimeIdentifier = $args[1]
$Platform = $args[2]
$ApplicationArguments = if ($args.Count -gt 4) { @($args[4..($args.Count - 1)]) } else { @() }
. (Join-Path $PSScriptRoot 'Wino.Debug.ps1')
Prepare-WinoDebugDeployment -ProjectPath $ProjectPath | Out-Null
$launch = @('run', $ProjectPath, '-c', 'Debug', '-r', $RuntimeIdentifier, '-p', "Platform=$Platform",
    '--no-build', '--no-restore', '--detach')
if (@($ApplicationArguments).Count -gt 0) { $launch += @('--') + $ApplicationArguments }
& winapp @launch
exit $LASTEXITCODE
