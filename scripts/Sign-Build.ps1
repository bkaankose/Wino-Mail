# Sign-Build.ps1
# Usage: pwsh -File scripts/Sign-Build.ps1 -ProjectPath Wino.Mail.WinUI/Wino.Mail.WinUI.csproj
# Builds a Debug|Release MSIX of Wino.Mail.WinUI and signs it with the dev pfx
# in Wino.Mail.WinUI/keys/WinoMailDev.pfx, then verifies.

[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64","x86","ARM64")]
    [string]$Platform = "x64",
    [string]$PfxPath = (Join-Path $PSScriptRoot ".." "Wino.Mail.WinUI" "keys" "WinoMailDev.pfx"),
    [string]$PfxPassword = "wino-dev",
    [string]$OutputDir = "artifacts/sign"
)

$ErrorActionPreference = "Stop"

# Build (no signing) with packaging enabled
dotnet build Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c $Configuration --no-restore `
    /p:Platform=$Platform /p:RuntimeIdentifier="win-$($Platform.ToLower())" `
    /p:GenerateAppxPackageOnBuild=true `
    /p:AppxPackageSigningEnabled=false `
    /p:UapAppxPackageBuildMode=SideloadOnly

# Locate produced MSIX
$msix = Get-ChildItem -Path "Wino.Mail.WinUI/bin/$Platform/$Configuration" -Recurse -Filter "*.msix" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No MSIX produced under Wino.Mail.WinUI/bin/$Platform/$Configuration" }

Write-Host "Built: $($msix.FullName)"

# Find signtool.exe (prefer x64)
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
    Where-Object { $_.DirectoryName -match "$Platform$" -or $_.DirectoryName -match "x64$" } |
    Sort-Object FullName | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found in Windows Kits\10\bin" }

Write-Host "Signing with $signtool"
& $signtool.FullName sign /fd SHA256 /f $PfxPath /p $PfxPassword $msix.FullName
if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

# Verify
& $signtool.FullName verify /pa $msix.FullName
if ($LASTEXITCODE -ne 0) { throw "signtool verify failed with $LASTEXITCODE" }

Write-Host ""
Write-Host "Signed and verified MSIX: $($msix.FullName)"
Write-Host "Install with:"
Write-Host "  Stop-Process -Name 'Wino.Mail','Wino.Server' -Force -ErrorAction SilentlyContinue"
Write-Host "  Add-AppxPackage -Path '$($msix.FullName)' -ForceApplicationShutdown"
