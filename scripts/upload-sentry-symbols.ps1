#requires -Version 7.0
<#
.SYNOPSIS
Uploads retained Wino release symbols to Sentry.
.DESCRIPTION
Uploads symbols for the exact build selected by Version. The symbols are
matched by debug identifiers; Version is retained for validation and audit
output. The script is intentionally separate from release packaging so a
discarded build does not upload anything automatically.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SymbolsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.0$') {
    throw 'Version must have four numeric components and a zero revision, such as 2.1.1.0.'
}

$symbolsRoot = [IO.Path]::GetFullPath($SymbolsPath)
if (-not (Test-Path -LiteralPath $symbolsRoot -PathType Container)) {
    throw "Symbols path does not exist: $symbolsRoot"
}

$symbolFiles = @(Get-ChildItem -LiteralPath $symbolsRoot -Recurse -File -Include '*.pdb', '*.appxsym')
if ($symbolFiles.Count -eq 0) {
    throw "No PDB or appxsym files were found under: $symbolsRoot"
}

$null = Get-Command sentry -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace($env:SENTRY_AUTH_TOKEN)) {
    throw 'SENTRY_AUTH_TOKEN is required for Sentry symbol upload.'
}

Write-Host "Uploading $($symbolFiles.Count) symbol files for wino-mail@$Version from $symbolsRoot"
& sentry debug-files upload `
    --org bkaankose `
    --project winomail `
    --wait `
    $symbolsRoot
if ($LASTEXITCODE -ne 0) {
    throw "Sentry symbol upload failed with exit code $LASTEXITCODE."
}
Write-Host "Sentry symbols uploaded for wino-mail@$Version." -ForegroundColor Green
