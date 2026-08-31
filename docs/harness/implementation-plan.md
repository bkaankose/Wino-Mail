# Wino Mail harness implementation plan

> Status: Active  
> Started: 2026-09-01  
> Constraint: Use repository tools and free local automation only.

## Outcome

The repository will provide one fast and trustworthy development entry point.
The entry point will cover discovery, compilation, tests, launch, UI scenarios, XAML formatting, and diagnostics.

The work will preserve the current Debug package identity and application data.
The work will not add paid services, background agents, or mandatory agent reviews.

## Delivery rules

- Add a command only when it replaces repeated manual work.
- Keep each command usable by developers and agents.
- Print the exact external command before it runs.
- Return a nonzero exit code for a definite failure.
- Keep fast verification separate from integration verification.
- Preserve unrelated worktree changes.

## P0: Trustworthy local loop

### P0.1 Common command entry point

Add `scripts/wino.ps1` with these first commands:

- `affected`
- `build`
- `test`
- `run`
- `debug`
- `ui`
- `xaml`

Acceptance criteria:

- Each build target uses the repository Debug and x64 rules.
- App launch uses WinApp project mode.
- Release launch remains unavailable.
- `-NoBuild` and `-Restore` are explicit choices.
- The help output contains one example for each command.

### P0.2 XAML Styler command-line integration

Pin `XamlStyler.Console` in the repository tool manifest.
Use the existing `Settings.XamlStyler` as the only formatting configuration.
The implementation follows the official [XAML Styler script integration](https://github.com/Xavalon/XamlStyler/wiki/Script-Integration) interface.

Add these commands:

```powershell
.\scripts\wino.ps1 xaml changed
.\scripts\wino.ps1 xaml changed -Check
.\scripts\wino.ps1 xaml all
.\scripts\wino.ps1 xaml all -Check
```

Acceptance criteria:

- A fresh checkout can use `dotnet tool restore`.
- The CLI and Visual Studio extension use four spaces and no tabs.
- `-Check` does not change files.
- `changed` includes tracked and untracked XAML files.
- The script excludes `bin` and `obj` directories.
- A formatting failure returns a nonzero exit code.

Baseline result:

- The repository contains 149 source XAML files in the four active UI roots.
- The pinned CLI reports that 50 files match the current configuration.
- Enforce changed files first. Do not create a 99-file formatting change during P0.
- Treat the pinned CLI result as authoritative when the editor extension differs.

### P0.3 Current-source UI verification

Change `tests/ui/Run-WinoUiTests.ps1` so its default path does not reuse a running app.
An explicit `-UseRunning` option will keep the old behavior.

Add a `-Scenario` filter and a `-List` option.
Remove the interactive pause from the default path.

Acceptance criteria:

- The default path stops the exact Wino Debug process before deployment.
- The default path builds, deploys, and launches through WinApp.
- `-NoBuild` deploys the existing Debug output through WinApp.
- `-UseRunning` never claims that it deployed current source.
- The result JSON records the commit, dirty state, process, window, and launch mode.
- Unknown scenario names fail before app launch.

### P0.4 Documentation routing

Update the root and controls guidance after the commands are stable.
Keep package safety and completion requirements in `AGENTS.md`.
Move repeated command syntax into harness documents.

Acceptance criteria:

- Guidance uses `scripts/wino.ps1` for normal commands.
- The design guide names WinApp project mode, not Visual Studio deployment.
- XAML changes require `xaml changed -Check` before handoff.

## P1: Deterministic control scenarios

### P1.1 Playground UI runner

Add a runner that builds and launches `Wino.Mail.Controls.Playground`.
Use the same result format as the main-app UI runner.

Acceptance criteria:

- The runner uses deterministic local sample data.
- A scenario selects one playground page and one state transition.
- State scenarios query UI Automation properties.
- Visual scenarios create screenshots only when layout or theme matters.

### P1.2 Scenario catalog

Add scenarios while a control changes.
Start with hover actions because recent sessions exposed this gap.

Initial scenarios:

- `hover-actions-visible-on-pointer-over`
- `hover-actions-read-toggle-two-cycles`
- `hover-actions-flag-toggle-two-cycles`
- `hover-actions-source-item-replacement`
- `hover-actions-recycled-container`
- `hover-actions-none-hidden`

Acceptance criteria:

- Each scenario has stable automation IDs.
- Each state scenario contains an assertion.
- Each scenario can run independently.

### P1.3 Diagnostics command

Add `scripts/wino.ps1 logs`.
Resolve the installed Debug package and current local log file.

Acceptance criteria:

- `-Tail` limits output.
- `-Since` limits entries by time when the log format permits it.
- `-Match` filters text without changing the log.
- The command prints the resolved log path.

### P1.4 Compact results

Write harness results under `artifacts/harness/`.

Acceptance criteria:

- Results contain command, target, start time, duration, exit code, and source commit.
- UI results also contain process ID, window handle, and scenario results.
- Console output stays short when the command succeeds.

## P2: Mechanical boundaries

### P2.1 Architecture tests

Add `tests/Wino.Architecture.Tests` with the existing xUnit stack.
Do not add an architecture-test package.

Initial rules:

- Controls Core has no WinUI reference.
- Reusable controls have no main-app reference.
- Domain has no Services, ViewModels, or UI reference.
- New `IValueConverter` types fail verification.
- Elements with `x:Load` also have `x:Name`.
- Required shell templates stay in their designated dictionary.

### P2.2 Unified changed-file verification

Add `scripts/wino.ps1 verify changed` after P0 and P1 stabilize.

The command will:

1. Read changed files.
2. Run XAML Styler in passive mode for changed XAML.
3. Run relevant static audits.
4. Use CodeGraph to identify affected tests.
5. Run the narrow project build and selected tests.
6. Print any required UI scenario without running a broad suite.

### P2.3 Fast pull-request workflow

Use the same local verification command in GitHub Actions.
Add path filters to avoid unnecessary WinUI work.

The workflow remains optional when hosted minutes are constrained.
The local command is the source of truth.

## P3: Smaller knowledge map

Start this work after P0 commands remain stable across normal tasks.

Tasks:

1. Reduce root `AGENTS.md` to routing, safety, architecture, and completion rules.
2. Keep controls-specific boundaries in `controls/AGENTS.md`.
3. Add short harness documents for tests, launch, and diagnostics.
4. Add a decision record only when a decision recurs.
5. Link reusable controls to playground pages and scenario names.

Do not target a line count before the executable commands replace the prose.

## Verification order

Use this order for each milestone:

1. Parse every changed PowerShell script.
2. Run help and list commands without app deployment.
3. Run XAML Styler against one file in passive mode.
4. Run changed-file XAML verification.
5. Run one narrow test through the common entry point.
6. Run one narrow build through the common entry point.
7. Run UI deployment only when UI-runner behavior changes.

## Completion record

Update this section after each milestone.

| Milestone | Status | Evidence |
| --- | --- | --- |
| P0.1 Common entry point | Complete | Help, Controls Core, full WinUI build, and focused controls tests passed. |
| P0.2 XAML Styler | Complete | Passive and active single-file runs passed. The full baseline was measured. |
| P0.3 Current-source UI verification | Complete | Scenario filtering, process replacement, deployment, StartupSmoke, and result metadata passed. |
| P0.4 Documentation routing | In progress | Guidance uses the harness for normal commands. Full instruction reduction remains pending. |
| P1 Deterministic scenarios | Pending | Not implemented. |
| P2 Mechanical boundaries | Pending | Not implemented. |
| P3 Smaller knowledge map | Pending | Not implemented. |
