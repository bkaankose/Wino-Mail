# Wino audit replay contract

This is an **agent-guided regression replay**, not a validated unattended test suite. It preserves the completed audit's scenario IDs and flow, corrects known automation errors, and requires new assertions on every run. Do not copy baseline outcomes into new results.

For the smaller scripted task/contact persistence and activation-routing suite, use [REGRESSION.md](REGRESSION.md).
That suite has its own finite scenario selection and does not claim the full baseline sequence or coverage.

## Start a repeat run

From repository root in PowerShell 7:

```powershell
$run = & ./scripts/ui-audit/New-WinoAuditRun.ps1
& ./scripts/ui-audit/Invoke-WinoRecorded.ps1 -RunDirectory $run -ScenarioId PRE-001 -ArgumentList @('--version')
& ./scripts/ui-audit/Invoke-WinoRecorded.ps1 -RunDirectory $run -ScenarioId PRE-001 -ArgumentList @('ui','list-windows','-a','Wino.Mail.WinUI','--json')
```

The initialization command only creates files. Read `run.json` for the unique prefix, accounts and photo. User can request: **“Replay the Contacts, To Do and activation audit using scripts/ui-audit/REPLAY.md.”**

## Recording requirements

- Every WinApp invocation MUST pass through `Invoke-WinoRecorded.ps1`, including discovery, focus, retries, screenshot, build/deploy and relaunch. Use `-ArgumentList` arrays; never construct a shell command string.
- `commands.jsonl` records a started and completed/interrupted event with the same ID. It retains exact executable/argument boundaries, working directory, timestamps, exit code, and stdout/stderr paths. A start without completion is an interrupted/unknown outcome, never a pass.
- Capture current HWND with recorded `list-windows`. Require exactly one intended window or explicitly resolve the correct one from its title/PID. Inspect again after restart/background restore.
- Resolve generated selectors from fresh `inspect`/`search` output. Prefer AutomationId; select accounts by name plus `ShellAccountSelector`. Never reuse baseline `itm-*`/`lbl-*` hashes or HWNDs.
- Focus before set-value and record that focus as its own command. Verify values, move focus to commit, wait for expected dialogs. A successful click/invoke is not an assertion.
- Before each scenario, persist scenario ID/account, intended changes and next action. Add intended record names to `records.json` BEFORE creating them, then mark them confirmed/removed after assertions. Persist PASS/FAIL/BLOCKED/UNSUPPORTED/NOT-RUN with evidence references to `results.jsonl`.
- Keep scenario IDs/order from `scenarios.json`. That file is the baseline ledger, including corrections; repeat logical scenarios, not failed automation attempts. All not-run/blocked baseline branches must stay explicit if unavailable again.

## Preflight

1. Read repository AGENTS.md and the applicable WinApp testing instructions; current repository package rules take priority over stale skill instructions.
2. Record WinApp version (>=0.6), git revision/status, manifest/installed identity+publisher, account names/provider support, theme, and photo existence. Do not print credentials or read unrelated user records.
3. Use existing Debug x64 identity; stop on publisher mismatch. Build/deploy using the repository-approved `winapp run` command through the recorder (increase TimeoutSeconds for build). No Release launch, clean/unregister, or second identity.
4. Run the automation-ID audit. Inventory actual UI actions and compare to the baseline. Newly exposed actions receive explicit new scenario IDs, not silent omission.

## Repeatable scenario sequence

Use names derived from `run.json.prefix`, e.g. `<prefix>-Personal-Task`. No existing user records may be edited/deleted. Within each account finish independent scenarios after failures; block a dependent branch when its setup is not proven.

### To Do — each configured account

1. Select To Do, resolve account by name. New list -> FolderTextBox -> PrimaryButton. Assert sidebar/title. Record `<prefix>-<account>-List`.
2. Create main task through focused ToDoNewTaskTextBox + Enter; assert row. Open detail, edit ToDoTaskTitle and notes, focus away, close/reopen, assert both values.
3. Add step; wait for ToDoStepTitle; focus and rename. Reopen and assert. Complete/reopen with toggle-state assertions. Delete with cancel/preserve then confirm/gone assertions.
4. Exercise importance detail/context, completion row/context, My Day add/remove, due Today/Tomorrow/custom/remove. Assert actual resulting values; Gmail persistence problems are FAIL, not expected-pass exceptions.
5. Rename list via header, overflow and sidebar context, asserting sidebar/title after each. Use distinct known names; update records.json only after verification.
6. Create group; rename via context; create child list; ungroup and assert child retained. Delete independent empty group. Collapse an expanded group before context click if its bounds include children. Test list/task movement only between this run's records.
7. Create a second task with add button. Delete a disposable task through context, cancel/confirm. Exercise text filter/no-match/clear, alphabetical ordering, active/completed/important filtering.
8. Select known audit row then Ctrl+A in the isolated task list. Assert exact expected count BEFORE bulk mutations. Stop dependent bulk tests on duplicates. Complete/reopen/mark-important/delete with state assertions, not merely button existence.
9. Open task-to-calendar draft where supported; assert title/date/notes then discard. Print/email placeholders remain UNSUPPORTED until implemented; never send messages.
10. Synchronize, navigate/reopen, restart through project-mode WinApp, and verify record identity/count/title/notes/steps/flags/deletion. Distinguish immediate UI, local persistence and independently verified server persistence.
11. Clean up only this run's lists/groups/tasks through UI; cancel/confirm list deletion. Verify absence after sync/restart. Document leftovers rather than touching storage directly.

### People — each applicable account

1. Select account; New Contact; fill unique DisplayName/GivenName and example.invalid email. Assert values before Save. Handle **Save contact to** destination picker explicitly and choose the intended account; assert saved contact.
2. Reopen/edit name and representative phone/email/work/notes fields; assert persistence. Exercise context edit, detail/context favorite roundtrip and Favorites view.
3. Personal photo: choose run.json.photoPath via file picker. Discover picker HWND separately through recorded WinApp list-windows. Assert saved photo visually, reopen/restart, remove photo, save/reopen and compare evidence. Do not replace visual assertions with file-existence checks.
4. Exercise optional email/phone/IM/relation add/remove. Dirty cancel: first cancel discard confirmation and assert edit retained; then confirm discard and reopen original value.
5. Create a uniquely named local contact list, rename, assign audit contact via editor/context, assert membership, remove and assert absence. Delete test list cancel/confirm.
6. CardDAV collection creation: unique name only. Server refusal is a provider limitation. Conflict choices remain BLOCKED unless a controlled synthetic conflict exists; do not force conflicts on user records.
7. Locate audit contact through search suggestions (typed input, settle, Enter, Escape); verify editor DisplayName before deletion. Reinspect list/account to locate a current row before context actions. Test detail/context deletion cancel/confirm. Check sync/restart absence; clean all audit contacts.

### Activation — finite 4 × 4 matrix

Use fixtures under the new run directory and `Replay-Activation.ps1` (log its invocation/output separately in the scenario result; all associated WinApp commands still use the recorder).

Inputs: valid VEVENT ICS, malformed ICS, webcal, webcals. States: running Calendar, running People, hidden shell with process alive, cold with test process absent.

1. Establish/record state. Close shell via WinApp. For cold cases, terminate only the identified test Wino PID after closing; preserve configured data. Never stop unrelated processes.
2. Replay targeted Windows activation with the helper, which keeps its caller alive five seconds. Accepted=true proves dispatch only.
3. Re-list windows, choose current HWND. Valid ICS: select destination calendar, assert imported title/time/location/description, discard. Malformed ICS: assert validation warning and usable shell. Protocols: assert Calendar routing; no subscription claim for .invalid URLs.
4. Repeat empty and encoded URIs. VTODO-only input is unsupported if VEVENT-only importer remains. Missing VCF/carddav/caldav registrations are UNSUPPORTED separately from parser/runtime bugs.
5. Ensure no draft was saved and record new windows/PIDs. Do not launch nonexistent associations through a different default app as a substitute.

## Pause/resume

When user says stop, stop issuing commands immediately. Create `PAUSE` in the run directory and cancel the active tool/process where possible. Recorder checks every 200 ms and kills its CLI process tree; an already dispatched UI mutation may still complete. Record that uncertainty. No auto-resume.

On explicit resume: inspect checkpoint and current UI/records, reconcile in-flight creation/deletion, then remove PAUSE. Never replay a mutation just because its completion event is missing. Resume at the first unverified logical step, using fresh selectors.

## Finish

Write report.md, scenario coverage/results, cleanup.md, and checkpoint.md in the run directory. Preserve all raw command output/evidence. Record discovered bugs without fixes. Report remaining coverage honestly and avoid an exhaustive-success percentage.

## Historical limitations

`historical-commands.jsonl` exports commands actually found in the original action log. The original helper joined arguments without quoting and did not record implicit focus, every retry, or all direct CLI calls. Therefore it cannot reconstruct every past command losslessly. It is evidence, NOT an executable script. The original audit also contained NOT-RUN routes; the expanded sequence above is a replay contract, not a claim those routes were executed before.

The recorder guarantees structured capture only for commands routed through it. This contract and recorder are versioned test tooling; an agent still resolves live controls and judges assertions. No unattended end-to-end replay certification is claimed.
