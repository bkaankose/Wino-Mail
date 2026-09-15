# Agent workflow review — 2026-09-15

## Scope

This review used five recent Wino tasks, repository instructions, the harness, and selected skill files.
Task summaries provide examples, not a complete history or a token-cost benchmark.

## Findings from recent tasks

| Task | Finding | Better request |
| --- | --- | --- |
| Align compact search bar left | The file links and expected position made this request focused. Runtime verification remained pending. | Add the required narrow-window state and whether current Debug deployment is available. |
| Fix flyouts, tray clicks, settings | Three independent behaviors shared one task. The no-launch instruction was clear. | Separate the menu migration, tray timing, and missing headers. Keep each completion criterion explicit. |
| Trace Gmail thread unread bug | Folder selection and conversation expansion were ambiguous. The assistant incorrectly removed sent replies. | State both invariants: folders select conversations, while each conversation includes unique messages across that account. |
| Fix Release build dependency | A passing compile missed a packaging regression. The intended installable output became clear later. | Name the required artifact and acceptance check: Store-identity bundle, signing, package generation, and installation scope. |
| Assess contacts and to-do UI tests | Deferring all tests delayed feedback. The reported replay used 2,159 WinApp commands. | Run each subtask's narrow checks immediately. Run the shared app audit after integration. |

The assistant owns incorrect assumptions, premature completion claims, and redundant verification.
Users do not need to prescribe implementation steps to compensate for these errors.

## Astra article

The article recommends narrow skill triggers, small skill routers, and references loaded only for the relevant workflow.
It also recommends removing outdated instructions and defining completion before work starts.
Repeated testing instructions can create extra work. Ambiguous approval boundaries can cause early stops.
These changes improve instruction quality without removing Wino-specific package and data constraints.

Source: [Rethinking skills and prompts for GPT-6 Astra](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra).

## Applied changes

- Reduced root `AGENTS.md` from 1,885 to 680 whitespace-delimited words, approximately 64%.
- Moved implementation rules and detailed runtime commands into task-specific references.
- Added an explicit repository override for the stale Visual Studio-only testing skill.
- Added a fallback for unrelated CodeGraph results, which occurred during PowerShell harness discovery.
- Added `affected -Path` to exclude unrelated dirty-worktree changes from an analysis request.
- Added `build app -Configuration Release` with runtime-command rejection of `-Configuration`.
- Added Git failure propagation during affected-file discovery.
- Added 17 harness checks with native tool doubles, requiring no app build or deployment.

The word reduction measures the root file only. It is not a measured reduction in total tokens or execution time.
Relevant implementation work still loads its required reference sections.
Personal skills and installed plugin files remain unchanged.

## Next improvements, in priority order

1. Repair the personal `wino-winapp-testing` skill. It still forbids `winapp run` and describes CLI 0.3.1 limitations.
   The repository override resolves this project conflict, but the global skill can still mislead other projects.
2. Turn frequent audit flows into executable scenarios with stable selectors and state assertions.
   Existing UI tests cover startup, settings, compose navigation, and mail rendering navigation.
   Contacts, To Do persistence, and activation audit flows still need agent-guided replay.
3. Establish a supported local Debug deployment path alongside the user's Store testing workflow.
   Diagnose package ownership and signatures before runtime work. Preserve the existing identity and application data.
4. Trim overlapping personal WinUI skill descriptions and use narrow triggers.
   Add a skill only for a recurring workflow that needs repository knowledge or reusable commands.
5. Measure a few comparable fixes before further tuning.
   Record elapsed time, tool calls, builds, tests, retries, and completion gaps. Compare token usage only where available.

## Prompt pattern

Use only the fields that add information:

> Fix [observed behavior] in [file or flow].
> Reproduce with [short sequence]. Expected: [visible result and invariants].
> Keep [specific exclusions]. Complete the change and affected checks.
> Runtime scope: [current Debug interaction, or compile only].

Example:

> Fix Gmail thread counts changing after read/unread or flag/unflag.
> Folders select conversations. Conversation contents include sent replies across the same account, with each Gmail message counted once.
> Keep counts stable through synchronization and folder navigation. Add focused regressions and verify the current Debug app.

For delegated work:

> Give each subtask a separate change area and immediate narrow checks.
> Integrate completed changes, then deploy Debug once and run the relevant shared UI scenarios.

## Verification and limits

All 17 harness checks passed. Command help, PowerShell parsing, local documentation links, and diff whitespace were checked.
The tests intercept native tools. They check arguments and failures without compiling or launching Wino.
No global model settings changed. No token savings or faster builds are claimed as measured results.

## Follow-up implementation — 2026-09-15

The requested follow-up added a read-only `doctor` command and package guards to `run`, `debug`, `ui`, and scripted audits.
It corrected the earlier assumption that matching name/publisher always permits Debug deployment.
The actual signed installation reports `IsDevelopmentMode=false`, and WinApp explicitly refused replacement without removal.
The user chose to preserve that installation and leave live verification pending.

The new [scripted regression subset](../../scripts/ui-audit/REGRESSION.md) covers task/step restart persistence, contact save/edit/restart/discard/deletion, and protocol routing.
It records exact commands and synthetic record states, rejects ambiguous selectors, and stops on failed assertions.
This subset does not replace the full provider/theme/activation audit.

Eight personal skills were updated: runtime testing, WinUI fixes, general WinUI architecture, localization, activation, IMAP, messenger, and memory debugging.
Descriptions are narrower, old repository paths are corrected, and the runtime skill now uses WinApp project mode.
The generic architecture skill no longer requires reselecting the framework or packaging model for routine Wino edits.
Plugin cache files were not modified. Original personal skills were copied under `artifacts/skill-review/20260915-123727/`.

A real blocked audit is recorded at `artifacts/ui-audit/20260915-124220-b57ef1/summary.json`.
It created no application records and left the original installed process running.
The new scripts have focused automated checks. Live UI selectors, restart scenarios, and visual themes remain unverified.
Final checks: 20 harness tests, 10 deployment tests, and 9 regression-helper tests passed. All eight updated skills passed validation.
