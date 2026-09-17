# Scripted Wino regression subset

`Run-WinoRegression.ps1` runs three finite scenarios with recorded commands and explicit state assertions.
It reuses the earlier audit flows with fresh run prefixes, exact account selection, and current window discovery.
The helpers have automated checks. Live scenario certification is pending the local Debug deployment conflict.

## Commands

From the repository root in PowerShell 7:

```powershell
./scripts/wino.ps1 doctor app
./scripts/wino.ps1 audit app -List
./scripts/wino.ps1 audit app -Account 'Personal' -ContactDestination 'Personal · Personal' -Theme Dark
./scripts/wino.ps1 audit app -Scenario TasksPersistence -Account 'Personal' -Theme Dark
./scripts/wino.ps1 audit app -Scenario ActivationRouting -Theme Dark
```

Use the exact visible account and contact destination labels for the chosen test account.
The examples are not account defaults. Contacts may be synchronized to that explicitly selected account.
Add `-Restore` when project/package inputs changed or restore assets are missing.
The runner always builds Debug once. Its restart checks reuse that same output.
`-NoBuild` and `-UseRunning` are not accepted for this runner.

Current navigation discovery uses the English mode names from the recorded baseline.
Use the English app language and a wide shell with visible account navigation for this subset.
`-Theme` records the caller-reported environment; it does not certify visual correctness or a theme matrix.

## Scenario contracts

| Scenario | Actions and assertions | Limits |
| --- | --- | --- |
| TasksPersistence | Create a unique list and task, edit title/notes, add a step, restart, require one matching row and saved values, cancel then confirm list deletion, restart and assert absence | Local restart persistence, not independent server verification |
| ContactsPersistence | Create a unique contact in an explicit destination, reopen structured name, save phone, restart, assert values, retain then discard edits, cancel then confirm deletion | Immediate deletion assertion; no photo, provider matrix, or independent server check |
| ActivationRouting | Switch from People through empty `webcal:` and `webcals:` activations, require one Calendar window and its new-event command | Routing only; no subscription, ICS import, background, or cold-start matrix |

The runner records intended names before creating data. It mutates only the current run's synthetic records.
It requires an exact editor name before contact edits/deletion and an exact list title before deleting the test list.
A duplicate selector, failed assertion, or ambiguous window stops the run.
Later scenarios remain `NOT-RUN`. No automatic cleanup runs after an uncertain failure.

## Evidence and recovery

Each run creates `artifacts/ui-audit/<timestamp>-<suffix>/`:

- `deployment-readiness.json`: identity and deployment blockers.
- `deployment.json`: successful current-source deployment, executable, and window evidence.
- `commands.jsonl` and `commands/`: exact WinApp argument arrays, exit codes, and output.
- `activations.jsonl`: dispatch and completion of protocol fixtures.
- `records.json`: intended, confirmed, and removed synthetic records.
- `summary.json` and `report.md`: scenario outcomes, duration, command count, limits, and remaining records.

If a run fails, inspect its ledger and the current UI before cleanup or another attempt.
Names marked `intended` may exist even when the CLI completion is missing.
Delete only records whose exact identity is established through the UI.

Create `PAUSE` in the run directory to stop further recorded commands.
A dispatched mutation may still finish. Remove `PAUSE` only after explicit resume and reconciliation.
Never replay a mutation solely because its completion event is absent.

For the full, agent-guided baseline and activation matrix, use [REPLAY.md](REPLAY.md).

## Script checks

These commands use test doubles and never interact with Wino:

```powershell
pwsh -NoProfile -File tests/scripts/Wino-Harness.Tests.ps1
pwsh -NoProfile -File tests/scripts/Wino-Debug.Tests.ps1
pwsh -NoProfile -File tests/scripts/Wino-Regression.Tests.ps1
```

They check command boundaries, package ownership, fail-fast behavior, ambiguous selectors, pause, and record transitions.
Passing helper checks does not certify that every UI selector or scenario works against the current app.
