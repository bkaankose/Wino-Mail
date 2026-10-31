<#
.SYNOPSIS
Runs the crash-recovery check, then starts the coordinator Claude Code session with the Discord channel attached.

.DESCRIPTION
Meant for the WinoWork-Boot scheduled task and for starting the coordinator by hand in a terminal that stays open.
Requires Bun on PATH, the discord@claude-plugins-official plugin, and a bot token in ~/.claude/channels/discord/.env.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workScript = Join-Path $PSScriptRoot 'work.ps1'
$prompt = Join-Path $PSScriptRoot 'coordinator.md'

foreach ($dir in @("$env:LOCALAPPDATA\Microsoft\WinGet\Links", "$env:USERPROFILE\.bun\bin", "$env:USERPROFILE\.local\bin")) {
    if ((Test-Path $dir) -and ($env:Path -split ';' -notcontains $dir)) { $env:Path = "$dir;$env:Path" }
}
if (-not (Get-Command bun -ErrorAction SilentlyContinue)) { throw 'Bun is not on PATH; the Discord channel plugin cannot start.' }

$pluginRoot = Get-ChildItem "$env:USERPROFILE\.claude\plugins\cache\claude-plugins-official\discord" -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
if ($pluginRoot) { bun install --cwd $pluginRoot.FullName --no-summary | Out-Null }

& $workScript boot
Set-Location $repoRoot
claude --channels plugin:discord@claude-plugins-official --dangerously-skip-permissions --append-system-prompt-file $prompt 'You are starting as the coordinator. Run boot and status as the role says, then wait for the owner.'
