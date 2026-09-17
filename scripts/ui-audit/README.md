# Repeat the Wino UI audit

Ask Codex: **Replay the Contacts, To Do and activation audit using scripts/ui-audit/REPLAY.md.**

[REPLAY.md](REPLAY.md) is the execution contract. It defines account setup, logical scenario order, assertions, failure recovery, pause/resume and cleanup. The runner remains agent-guided because controls/windows must be resolved from current UI state. It is not an unattended one-command suite.

| File | Purpose |
|---|---|
| New-WinoAuditRun.ps1 | Creates isolated artifacts/config with a unique record prefix; no app interaction |
| Invoke-WinoRecorded.ps1 | Records every routed WinApp invocation with lossless argument arrays, output, exit code and checkpoint |
| scenarios.json | 167 original scenario IDs and baseline outcomes; each new run starts not-run |
| historical-commands.jsonl | 1,356 command lines extracted from the original log; historical evidence, not executable |
| Replay-Activation.ps1 and fixtures/ | Reusable targeted Windows activation inputs/launcher |
| Test-Recorder.ps1 | Read-only recorder checks; no application data changes |

Initialize a run, then follow REPLAY.md. Route all commands through the recorder, including read-only discovery and retries. Logs are under `artifacts/ui-audit/<timestamp>-<unique-id>/`.

Historical logging did not preserve every argument boundary or all implicit/direct commands. Those gaps cannot be recovered retroactively. Future commands are lossless only when routed through the recorder. A dispatched mutation can complete after interruption; reconcile before retrying.

Validation performed: PowerShell parsing; real `winapp --version`; invalid-argument capture with spaces, quotes and literal shell metacharacters; output files and exit code; PAUSE prevents dispatch. Full end-to-end replay was not rerun as part of packaging.

## Scripted regression subset

Use [REGRESSION.md](REGRESSION.md) for finite task/contact persistence and protocol-routing checks:

```powershell
./scripts/wino.ps1 audit app -List
```

This runner builds current Debug source once and records each WinApp invocation.
Its helper checks pass; live scenario certification is pending a compatible Debug development environment.
Use REPLAY.md for the complete agent-guided baseline.
