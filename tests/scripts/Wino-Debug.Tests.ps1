#requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Wino.Debug.ps1')
$passed = 0
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Test-Case([string]$Name, [scriptblock]$Action) { & $Action; $script:passed++; Write-Host "PASS $Name" }
function New-Package([bool]$Development = $true, [string]$Publisher = 'CN=Test') {
    [pscustomobject]@{ Name='WinoTest'; Publisher=$Publisher; Version='1.0.0.0'; PackageFamilyName='WinoTest_family'; InstallLocation='D:/Wino/bin/Debug'; IsDevelopmentMode=$Development; SignatureKind='Developer' }
}
function Assess([object[]]$Packages = @(), [string]$Version = '0.6.0') {
    Get-WinoDebugAssessment -ProjectPath 'D:/Wino/App.csproj' -Name WinoTest -Publisher 'CN=Test' -ManifestVersion '1.0.0.0' -WinAppVersion $Version -InstalledPackages $Packages
}
Test-Case 'Fresh profile permits the checked-in identity' { Assert-True (Assess).Ready 'Fresh profile rejected.' }
Test-Case 'Existing development package passes' { Assert-True (Assess @(New-Package)).Ready 'Development package rejected.' }
Test-Case 'Signed installed package blocks even with matching publisher and version' {
    $result = Assess @(New-Package $false)
    Assert-True (-not $result.Ready -and $result.Code -eq 'InstalledPackageConflict') 'Signed package accepted.'
}
Test-Case 'Publisher mismatch blocks' {
    Assert-True ((Assess @(New-Package $true 'CN=Other')).Code -eq 'IdentityMismatch') 'Mismatch accepted.'
}
Test-Case 'Ambiguous development registrations block' {
    Assert-True ((Assess @((New-Package),(New-Package))).Code -eq 'AmbiguousRegistration') 'Ambiguous registration accepted.'
}
foreach ($version in @('', 'bad', '0.3.1')) {
    Test-Case "Unsupported WinApp version '$version' blocks" {
        Assert-True ((Assess -Version $version).Code -eq 'WinAppUnavailable') 'Unsupported CLI accepted.'
    }
}
Test-Case 'Future compatible WinApp version passes' { Assert-True (Assess -Version '0.7.0').Ready 'New CLI rejected.' }
Test-Case 'Assessment does not mutate its package input' {
    $package = New-Package $false
    $before = $package | ConvertTo-Json
    Assess @($package) | Out-Null
    Assert-True (($package | ConvertTo-Json) -ceq $before) 'Assessment mutated input.'
}
Write-Host "$passed deployment checks passed. No package operations ran."
