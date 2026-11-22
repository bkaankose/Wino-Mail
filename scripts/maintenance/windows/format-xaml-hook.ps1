# Claude Code PostToolUse hook: formats a .xaml file right after an agent writes it.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$filePath = $payload.tool_input.file_path

if ([string]::IsNullOrWhiteSpace($filePath) -or -not $filePath.EndsWith(".xaml", [StringComparison]::OrdinalIgnoreCase)) {
    exit 0
}

if (-not (Test-Path -LiteralPath $filePath)) {
    exit 0
}

$output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot "format-xaml.ps1") -Path $filePath 2>&1
if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine("XAML Styler could not format $filePath`n$($output | Out-String)")
    exit 2
}
