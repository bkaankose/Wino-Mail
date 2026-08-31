# Wino Mail harness review

> Status: Review baseline, 2026-08-31  
> Scope: Main app, reusable controls, local development tools, tests, diagnostics, and repository guidance  
> Goal: Reduce iteration time and agent token use without paid services or extra model calls

## Executive summary

Wino Mail already has more harness infrastructure than many single-developer projects.
It has scoped agent guidance, CodeGraph, a control playground, 851 test cases, WinApp automation, and a Native AOT gate.

The main problem is not a lack of instructions. The repository has many good instructions, but too few single-command feedback loops.
Agents must translate prose into long commands and manually decide which verification path applies.
This translation repeats in each task and consumes tokens.

The highest-value work is small and local:

1. Add one PowerShell entry point for build, test, run, UI verification, and logs.
2. Make UI verification reject stale binaries.
3. Add focused UI scenarios for the control playground.
4. Convert repeated architecture and XAML rules into scripts or tests.
5. Reduce the root `AGENTS.md` to a map of those executable paths.

Do not copy OpenAI's complete system. Wino Mail does not need background reviewers, recurring agents, or a local observability platform.
A single developer gets more value from deterministic scenarios, fast scripts, and narrow tests.

## Review basis

This review uses these repository sources:

- [`AGENTS.md`](../AGENTS.md)
- [`controls/AGENTS.md`](../controls/AGENTS.md)
- [`docs/wino-design-guideline.md`](wino-design-guideline.md)
- [`tests/ui/Run-WinoUiTests.ps1`](../tests/ui/Run-WinoUiTests.ps1)
- [`scripts/audit-xaml-automationids.ps1`](../scripts/audit-xaml-automationids.ps1)
- [`scripts/audit-xaml-accessibility.ps1`](../scripts/audit-xaml-accessibility.ps1)
- The test projects and GitHub Actions workflows
- A local report for one recent Wino Mail agent session
- Recent Codex task history for this repository

The external comparison uses OpenAI's [Harness engineering](https://openai.com/index/harness-engineering/) article.

The review did not run the full application or change product code.
It inspected the harness and its prior use.

## What OpenAI means by harness engineering

The article describes a repository that makes product behavior legible and enforceable for agents.
Its important practices are:

- A short `AGENTS.md` is a map, not an encyclopedia.
- Versioned repository documents are the system of record.
- Architecture boundaries are strict and mechanically enforced.
- Agents can launch and drive one isolated app instance per worktree.
- Logs, metrics, traces, and UI state are directly readable by agents.
- Plans, product specifications, design decisions, and debt remain in the repository.
- Repeated review findings become lints, tests, or tools.
- Small, continuous cleanup prevents weak patterns from spreading.

OpenAI optimizes for a team with very high agent throughput.
Wino Mail needs a different balance because human attention and tokens are both scarce.

## Current Wino Mail strengths

| Area | What Wino Mail does well | Benefit |
| --- | --- | --- |
| Repository entry point | The root guidance names the active WinUI app and rejects the deprecated UWP app. | Agents start in the correct project. |
| Scoped guidance | `controls/AGENTS.md` adds rules only for reusable controls, Core, Editor, and the playground. | Controls get more precise context. |
| Code navigation | Both files require CodeGraph before broad text searches. | Agents can find call paths and affected tests with less reading. |
| Build modes | Debug, Release, restore, package, and runtime rules are explicit. | Agents avoid unsafe package operations. |
| Launch safety | WinApp project mode preserves the checked-in package identity and user data. | Local verification does not create a second app identity. |
| UI legibility | WinApp can inspect, invoke, query, screenshot, and record the app. | Agents can verify behavior instead of reporting compilation only. |
| Test base | The repository has 851 xUnit test attributes across its test projects. | Core behavior already has substantial automated coverage. |
| Control boundary | `Wino.Mail.Controls.Core` is platform-neutral and the playground uses deterministic local data. | Much control logic can run without the full mail app. |
| Verification matrix | The root guidance maps change types to expected verification. | The required evidence is clearer than a generic "run tests" rule. |
| Design baseline | `docs/wino-design-guideline.md` defines product principles, density, surfaces, states, accessibility, and WinUI rules. | The repository now has a product-specific design baseline. |
| Native AOT | Pull requests run a warning-clean Native AOT publish and SQLite smoke harness. | Release-only compatibility faults have a mechanical gate. |
| Local diagnostics | Debug builds write Serilog output to a local rolling file and debugger output. | The raw data for local diagnosis already exists. |

These practices already match much of OpenAI's direction.
The repository is strongest where a rule has a tool behind it.

## Current costs and risks

### 1. `AGENTS.md` is becoming the manual

The root file is 212 lines and the controls file is 109 lines.
The root file includes complete commands, package safety rules, UI commands, architecture notes, and coding rules.

This detail is useful, but it has three costs:

- Every task pays the same context cost.
- Commands appear in more than one repository document and can drift.
- Agents must still assemble the commands and select the correct path.

OpenAI reports that its large instruction file failed for the same reasons.
Its replacement is a short map into focused documents and executable tools.

**Recommendation:** Keep the root file between 70 and 100 lines.
Move command details into scripts and focused documents.
Keep only routing rules, safety boundaries, and completion criteria in the root file.

### 2. Many important rules exist only as prose

Examples include:

- ViewModels must not contain service operations.
- Messenger registration must use the lifecycle methods.
- UI-bound messenger work must enter the dispatcher.
- Reusable Core code must remain platform-neutral.
- Controls must not reference the main app.
- Visual composition must remain in XAML.
- New `IValueConverter` implementations are forbidden.
- `x:Load` requires `x:Name`.
- Certain shell templates must stay in one resource file.

Agents can miss prose during a large task.
The compiler does not enforce most of these rules.

**Recommendation:** Add simple repository tests before custom analyzers.
Use xUnit or PowerShell to inspect project references, namespaces, and XAML patterns.
These tests require no new service and no paid tool.

### 3. Launch and verification commands are too long

A normal app run repeats the project path, architecture, runtime, restore, package, signing, detach, and JSON arguments.
The playground uses a similar command with a different project path.

Long commands create three costs:

- Agents spend tokens reproducing known configuration.
- A missed flag can deploy or verify the wrong output.
- Each retry includes command construction and output interpretation.

**Recommendation:** Add a single `scripts/wino.ps1` command with stable verbs.
Examples appear later in this review.

### 4. The current UI runner can verify stale code

`tests/ui/Run-WinoUiTests.ps1` reuses a running Wino Mail window.
It does this even when the caller did not pass `-NoBuild`.
The script tells the developer to close the app when a rebuild is required.

This behavior conflicts with the repository completion rule.
That rule requires proof that project mode deployed the current source.

A passing run can therefore describe an older binary.
This is the most serious harness defect found in this review.

**Recommendation:** Make current-source verification the default.
The runner must close the known Debug app through WinApp, build, deploy, and launch it.
Only an explicit `-UseRunning` option can reuse an existing process.
Write the source commit and build timestamp into the result JSON.

### 5. UI coverage is useful but narrow and stateful

The main UI runner has five scenarios:

- Startup
- Settings mode
- Language switching
- Compose navigation
- Mail rendering navigation

These scenarios use the installed app and its current account data.
Some selectors depend on element position, generated IDs, or visible language.
The language scenario changes a user setting and then attempts to restore it.

This design is appropriate for a smoke suite, but not for fast control development.
It does not provide focused playground scenarios for control states.

**Recommendation:** Separate two UI lanes:

- `ui:playground` uses deterministic data and verifies one control family.
- `ui:app-smoke` uses the installed app and verifies only critical integration paths.

The playground lane must be the default for reusable control work.

### 6. Static XAML checks do not cover both UI roots

The automation-ID script scans `src/Wino.Mail.WinUI` by default.
It does not scan `controls/Wino.Mail.Controls` or the playground unless a caller supplies another root.

The accessibility script also defaults to the main app.
It emits findings but does not fail when it finds one.

This conflicts with the controls guidance, which requires stable automation metadata and accessible behavior.

**Recommendation:** Add a wrapper that runs both scripts against all XAML roots.
Make the accessibility audit return a nonzero exit code for definite errors.
Keep advisory findings as warnings.

### 7. The fast pull-request lane is missing

The only automatic pull-request workflow is the Native AOT workflow.
It restores and publishes the full app in Release mode.
This is valuable but expensive and slow.

There is no visible pull-request workflow for:

- A Debug compile
- Unit tests
- XAML audits
- Architecture tests
- Documentation-link checks

**Recommendation:** Add a fast workflow before the AOT workflow.
Use path filters and the same local `scripts/wino.ps1 verify changed` command.
If hosted minutes are constrained, run this command locally before push.

### 8. Logs exist, but the harness does not expose them

The app writes Debug logs to the local app folder through Serilog.
WinApp `--debug-output` captures launch and crash diagnostics.

Agents still need to discover the package path and locate the current log file.
There is no repository command to tail, filter, or collect logs for one run.

**Recommendation:** Add `scripts/wino.ps1 logs`.
It can resolve the package, print the log path, and filter by time or text.
Use built-in PowerShell and the existing log format.
Do not add a metrics stack.

### 9. Project boundaries are descriptive, not structural

The solution separates Domain, Core, Services, ViewModels, UI, Messages, and controls.
That separation is helpful, but project references still permit broad dependency paths.

For example, a project boundary does not prove that a ViewModel avoids business operations.
It also does not prove that UI code enters a dispatcher after background work.

**Recommendation:** Define a small dependency policy and verify it in tests.
Start with project-reference direction because it is cheap and stable.
Add source-pattern rules only for faults that recur.

### 10. Repository knowledge is thin outside the guidance files

The `docs` directory contains a strong design baseline and one prototype.
It does not yet contain an indexed architecture map, short decision records, feature specifications, or a verification ledger.

The design gap that existed in older sessions is partly closed.
The current design guideline is a useful baseline.
It still needs reusable component examples and links to verified playground pages.

One line in the design guide also says that Visual Studio deploys the app.
The current root guidance uses WinApp project mode.
This is a small example of documentation drift.

**Recommendation:** Add only documents that remove repeated explanation.
Do not create a large documentation program.

## Evidence from recent development sessions

One recent hover-actions transcript gives a useful baseline:

| Measure | Result |
| --- | ---: |
| Duration | 6.3 minutes |
| Parent turns | 43 |
| Combined output tokens | 73,664 |
| Cache-read tokens | 4,173,083 |
| Build attempts | 0 |
| Skills used | 0 |

The session repeatedly read related files with `cat`, `grep`, and `sed`.
It read the root guidance near the end and never reached a build.

Recent Codex history shows a second pattern around the same feature:

1. Prototype hover actions.
2. Implement hover actions.
3. Fix missing hover actions.
4. Revert the hover actions.
5. Refactor the hover-actions control.

Later tasks used CodeGraph, a focused playground, WinApp, and direct state queries.
Those tasks produced better evidence, but still repeated long build and launch commands.

### Interpretation

The main token loss comes before coding and after the first UI failure.
The repository does not provide a named scenario that reproduces one control state from one command.

The agent then performs these operations manually:

- Reconstruct the binding path.
- Find sample data.
- Build the playground.
- Close or reuse a process.
- Launch the correct package.
- Find stable selectors.
- Perform state changes.
- Query the resulting state.

Each manual operation creates more reasoning, command text, and failure output.

The best response is not another review agent.
The best response is a reusable scenario such as `hover-actions-toggle-and-recycle`.

## Recommended target harness

### One entry point

Add `scripts/wino.ps1` with a small command surface:

```powershell
# Discover affected projects and tests.
.\scripts\wino.ps1 affected

# Compile the narrow affected target.
.\scripts\wino.ps1 build controls
.\scripts\wino.ps1 build playground
.\scripts\wino.ps1 build app

# Run narrow tests. Reuse output after the first successful build.
.\scripts\wino.ps1 test changed
.\scripts\wino.ps1 test controls -Filter HoverAction

# Launch the current Debug output.
.\scripts\wino.ps1 run app
.\scripts\wino.ps1 run playground -NoBuild
.\scripts\wino.ps1 debug app

# Run one deterministic UI scenario.
.\scripts\wino.ps1 ui playground -Scenario hover-actions-toggle-and-recycle
.\scripts\wino.ps1 ui app -Scenario compose-navigation

# Read diagnostics from the current Debug package.
.\scripts\wino.ps1 logs -Tail 100
.\scripts\wino.ps1 logs -Since 10m -Match "HoverAction"

# Run the complete local verification for changed files.
.\scripts\wino.ps1 verify changed
```

The script must print the commands that it runs.
It must also write a short JSON result under `artifacts/harness/`.
This output gives agents a compact source of truth.

### Three verification lanes

| Lane | Target time | Contents | Use |
| --- | ---: | --- | --- |
| Edit lane | Under 20 seconds | Affected project compile or one test class | After each logical change |
| Scenario lane | Under 90 seconds | Playground launch plus one UI scenario | After a control or visual-state change |
| Integration lane | Several minutes | Main app build, selected UI smoke, AOT when relevant | Before handoff or release |

Do not run the integration lane after each edit.
Do not report UI behavior from the edit lane.

### Deterministic playground scenarios

Each public control family needs a stable page and named scenarios.
A scenario must use local data and stable automation IDs.

For hover actions, the minimum scenario set is:

- `hover-actions-visible-on-pointer-over`
- `hover-actions-read-toggle-two-cycles`
- `hover-actions-flag-toggle-two-cycles`
- `hover-actions-source-item-replacement`
- `hover-actions-recycled-container`
- `hover-actions-none-hidden`
- `hover-actions-light-dark-contrast`

The state-changing scenarios must query the final toggle state.
A screenshot is optional unless layout or theme is the subject.

### Cheap architecture verification

Create `tests/Wino.Architecture.Tests` with no new package beyond the current xUnit stack.
Start with these rules:

- `Wino.Mail.Controls.Core` does not reference WinUI assemblies.
- `Wino.Mail.Controls` does not reference `Wino.Mail.WinUI`.
- `Wino.Core.Domain` does not reference Services, ViewModels, or UI.
- `Wino.Messages` contains message contracts only.
- Non-English translation files do not change in normal feature work.
- New `IValueConverter` classes fail verification.
- XAML elements with `x:Load` also have `x:Name`.
- The required shell templates remain in the designated resource dictionary.

Do not encode subjective style as source-pattern tests.
Encode dependency direction and repeated failure patterns.

### Small repository knowledge map

Use this structure:

```text
AGENTS.md                         Short routing and safety map
controls/AGENTS.md                Controls-specific routing and safety map
docs/
  architecture.md                Projects, dependency directions, runtime flows
  design/
    index.md                      Link to the current Wino design baseline
    components.md                 Reusable components and playground examples
  harness/
    testing.md                    Verification lanes and scenario catalog
    debugging.md                  Logs, WinApp debug output, common failure routes
    launching.md                  App and playground launch behavior
  decisions/
    0001-*.md                     Short records for decisions that agents often revisit
```

Do not duplicate commands in these files.
Link to `scripts/wino.ps1 -Help` for executable details.

## Proposed changes to the guidance files

### Root `AGENTS.md`

Keep:

- Active app and deprecated-project warning
- CodeGraph-first routing
- Dirty-worktree safety
- Package identity and Release safety
- Verification matrix
- Core architecture boundaries
- Links to design and controls guidance
- Completion evidence requirements

Move:

- Full command lines into `scripts/wino.ps1`
- Detailed launch and UI syntax into `docs/harness/`
- Long architecture explanations into `docs/architecture.md`
- Detailed XAML patterns into design and controls documents

Replace repeated rules with executable commands:

```text
Before editing: .\scripts\wino.ps1 affected
Before handoff: .\scripts\wino.ps1 verify changed
For UI work: .\scripts\wino.ps1 ui <target> -Scenario <name>
```

### Controls `AGENTS.md`

Keep its scope and control-boundary decisions.
They are clear and useful.

Replace the four build commands with the common script.
Link each public control to its playground page and scenario prefix.
Require the deterministic playground lane before main-app integration.

## Priority plan

### P0: Remove false verification and command repetition

Estimated effort: one day.

1. Correct `Run-WinoUiTests.ps1` so the default path deploys current source.
2. Add a scenario filter and remove the interactive pause by default.
3. Add `scripts/wino.ps1` for app and playground build, run, and debug commands.
4. Pin XAML Styler and add active and passive commands for changed XAML.
5. Add one wrapper for XAML audits across all UI roots.
6. Correct the Visual Studio deployment reference in the design guide.

Expected result: fewer launch retries, less command text, and trustworthy UI results.

### P1: Make control work deterministic

Estimated effort: one to two days, added gradually.

1. Add a playground UI runner.
2. Add scenarios for the control currently under development.
3. Add `test changed` using CodeGraph output.
4. Add `logs` to resolve and read the current Debug log.
5. Store compact JSON results for agent consumption.

Expected result: most control changes remain inside a fast, local loop.

### P2: Enforce stable boundaries

Estimated effort: a few hours for the first rules.

1. Add project dependency tests.
2. Add XAML rules for known recurring faults.
3. Add a fast local verification command.
4. Use the same command in a pull-request workflow when hosted minutes permit it.

Expected result: fewer review turns and less architecture drift.

### P3: Shrink and index repository knowledge

Estimated effort: incremental.

1. Reduce the root guidance after the scripts exist.
2. Add one architecture map.
3. Add a short decision record only when a choice recurs.
4. Add component examples while a control is already changing.

Expected result: lower context cost without a documentation maintenance project.

## Practices not worth copying now

OpenAI's system includes practices that fit a large agent-first team.
They do not fit Wino Mail's current constraints.

| OpenAI practice | Wino Mail decision | Reason |
| --- | --- | --- |
| Multiple agent reviewers per change | Do not adopt by default. | It spends tokens and adds little value for narrow, deterministic checks. |
| Recurring documentation agents | Do not adopt. | A single developer can update one nearby document during the related change. |
| Recurring cleanup agents | Do not adopt. | Add a failing rule only after a pattern recurs. |
| Per-worktree metrics and trace platform | Do not adopt now. | Local Serilog files and WinApp debug output cover the immediate need. |
| Minimal blocking merge gates | Do not copy directly. | A single developer has lower correction throughput and needs reliable local gates. |
| Fully agent-generated review and merge | Do not adopt. | Human product judgment remains the scarce and valuable resource. |
| Large quality-score system | Defer. | A short list of known verification gaps is sufficient. |

## Cost controls for agent use

Use these rules to reduce token consumption:

1. Start each task with one acceptance scenario.
2. Run `affected` before reading broad folders.
3. Use CodeGraph once with named symbols and the expected behavior.
4. Build the narrow project before reading more code after the first hypothesis.
5. Use `--no-build --no-restore` only after a successful build of the same output.
6. Use one UI scenario, not a full UI sweep, during implementation.
7. Query properties for state behavior. Use screenshots only for visual behavior.
8. Escalate from playground to the main app only after the playground passes.
9. Convert a repeated fault into a test or script before adding more prose.
10. Do not request reviewers or documentation analysis for a deterministic change.

## Suggested task template

Use a small prompt or issue template:

```markdown
## Outcome
Describe one user-visible or technical result.

## Acceptance scenario
Name the page, initial state, action, and expected observable result.

## Scope
List the likely projects or controls. State exclusions.

## Verification budget
Name the narrow test, playground scenario, or main-app smoke scenario.

## Product decision
Link the design rule or record. Add one sentence only when no rule exists.
```

This template reduces speculative exploration.
It also makes the completion condition visible before implementation starts.

## Success measures

Measure the harness for one month with local JSON results.
No external service is required.

| Measure | Initial target |
| --- | ---: |
| Time from edit to first compile result | Under 20 seconds for Core and ViewModel work |
| Time from edit to playground scenario result | Under 90 seconds |
| UI runs that prove current-source deployment | 100% |
| Repeated manual build or launch command variants | Zero in agent transcripts |
| Control UI changes first verified in playground | More than 80% |
| New recurring faults converted to executable verification | One rule per repeated fault |
| Root `AGENTS.md` length | 70 to 100 lines after migration |

Do not optimize token counts alone.
The useful measure is the number of verified development cycles per task.

## Final assessment

Wino Mail is not far from an effective single-developer harness.
Its guidance already contains strong domain knowledge and safe WinUI workflows.

The next improvement is execution, not more advice.
Make the correct path one command, make stale verification impossible, and keep control work inside deterministic playground scenarios.

That approach applies OpenAI's core lesson without copying its cost structure:
when a task fails, improve the repository capability that made the failure expensive.
