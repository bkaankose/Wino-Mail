#Requires -Version 5.1
<#
.SYNOPSIS
    Installs a Mail-Cal release that is signed with the self-signed Mail-Cal certificate.

.DESCRIPTION
    Download the .msixbundle, the .cer file and this script from the same GitHub release into one folder, then run:

        powershell -ExecutionPolicy Bypass -File .\Install-MailCal.ps1

    The script asks for administrator rights once, adds the Mail-Cal certificate to the computer's
    Trusted People store, and installs or updates Mail-Cal for the current user. Your accounts and data
    are kept when you update.
#>
param(
    [string]$Path = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$certificate = Get-ChildItem -LiteralPath $Path -Filter *.cer -File | Select-Object -First 1
$bundle = Get-ChildItem -LiteralPath $Path -Filter *.msixbundle -File | Sort-Object Name -Descending | Select-Object -First 1
if (-not $certificate) { throw "No .cer file found in '$Path'. Download it from the same release as this script." }
if (-not $bundle) { throw "No .msixbundle file found in '$Path'. Download it from the same release as this script." }

$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.FullName)
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint

if (-not $trusted) {
    Write-Host "Trusting the Mail-Cal certificate ($($cert.Subject), $($cert.Thumbprint))..."
    $import = "Import-Certificate -FilePath '$($certificate.FullName)' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $process = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-Command', $import
    if ($process.ExitCode -ne 0) { throw 'Could not add the certificate. Administrator rights are required once.' }
}

Write-Host "Installing $($bundle.Name)..."
Add-AppxPackage -Path $bundle.FullName -ForceApplicationShutdown
Write-Host 'Mail-Cal is installed. Open it from the Start menu.'
