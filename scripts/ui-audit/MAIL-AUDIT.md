# Mail mode audit contract

This suite is an agent-guided live audit. It does not certify unattended execution.
Use only audit messages. Keep application code unchanged. Record product defects for later work.

## Start

Run these commands from the repository root in PowerShell 7:

```powershell
$run = & ./scripts/ui-audit/New-WinoMailAuditRun.ps1
Import-Module ./scripts/ui-audit/MailAudit.Common.psm1 -Force
./scripts/wino.ps1 doctor app | Set-Content "$run/deployment-readiness.json"
Invoke-MailAuditCommand $run PRE-001 @('--version')
Invoke-MailAuditCommand $run PRE-001 @('run','src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj','-c','Debug','-r','win-x64','--no-restore','-p','Platform=x64','-p','GenerateAppxPackageOnBuild=false','-p','AppxPackageSigningEnabled=false','--detach','--json') -TimeoutSeconds 1200
Invoke-MailAuditCommand $run PRE-001 @('ui','list-windows','-a','Wino.Mail.WinUI','--json')
```

Stop before deployment if doctor reports a blocker. Restore first when project inputs changed or restore assets are missing.
Record git revision/status, manifest identity, deployment output, and the current window.
Never replace the identity, unregister packages, clean data, or launch Release.
Use only recorded `winapp ui` commands for application interaction and screenshots.

## Discover accounts and settings

1. Inspect the shell through the current HWND.
2. Select Mail mode.
3. Record every configured account, provider, and sending address in `run.json.accounts`.
4. Use objects with `name`, `address`, `provider`, and `recipient` fields.
5. Sort distinct addresses alphabetically and connect each address to the next address, including last to first.
6. Record duplicate-address accounts separately. Select another distinct address as their recipient.
7. If fewer than two distinct addresses exist, block cross-account scenarios.
8. Record the language, theme, original threading setting, and actual enabled shortcuts.
9. Run `scripts/audit-xaml-automationids.ps1` and save its output.

Do not configure new accounts. If account discovery fails, retain `UNDISCOVERED` coverage and document the blocker.
Resolve selectors from fresh UI output. Never reuse historical hashes or HWNDs.
Use exact account names and `ShellAccountSelector` where available.
Check composer From and all recipients before each send. All recipients must belong to configured accounts.

## Execute the matrix

`mail-scenarios.json` defines stable IDs, prerequisites, fixtures, steps, assertions, evidence, and recovery.
Expand every account scenario for every configured account. Execute global scenarios once.
Create all independent seed messages before mutation scenarios. This avoids testing replies against deleted fixtures.
Use a separate original/reply pair for thread deletion. Keep the main conversation for persistence checks.
For the threading setting test, require a proven multi-message conversation even though its account varies.

Create fixtures through `Add-MailAuditFixture` before UI creation. Use its exact subject and body token.
Before sending, call `Set-MailAuditFixture` with `send-intended`. A second send attempt must fail closed.
Record observed Sent and received copies separately in result evidence. Keep the fixture's last observed state in its ledger.
If a send outcome is uncertain, inspect Drafts, Outbox, Sent, and the recipient mailbox before further action.
Never repeat the send to solve an assertion timeout.

Focus before value entry. Read the value back, then move focus to commit it.
Use `send-keys --via send-input` for shortcuts and rich-text entry.
Inspect the file picker's HWND separately. Select the supplied image through its filename control.
Use 10-second UI waits and 120-second delivery waits. Record each synchronization request and each polling command.
Do not treat command success as an assertion. Verify resulting content, state, and folder membership.

For read state, avoid opening the reader before the unread assertion. Opening mail can mark it read.
For bulk actions, require exactly two selected audit identities before every mutation.
Never use select-all in an ordinary mailbox. Never empty folders or permanently delete messages.
For categories, use existing definitions only. For Move, use existing destinations only.
Inventory all context actions. Record unsupported, excluded, and inaccessible actions explicitly.
Exclude permanent deletion, sender rules, and debug notification actions from mutation coverage.

## Settings, persistence, and visual evidence

Enable threading for conversation scenarios when needed. Preserve the original value for restoration.
Toggle threading off once through `MessageListPageToggleSwitch3`. Inspect the same messages as separate rows.
Toggle it on again and inspect regrouping. Restore the original value after the audit.
After state checks, restart the same Debug output once through project mode with `--no-build --no-restore`.
Resolve the new HWND before persistence checks. Do not claim server persistence from a local restart.

Capture screenshots after meaningful visual transitions. Inspect each screenshot for clipping, overlap, stale content, and focus.
Record the account, scenario, theme, PID/HWND, and evidence paths in each result.
Record UI, post-sync, restart, and cross-account receipt as separate stages.
Use `Add-MailAuditResult` for each observed outcome. PASS requires assertion evidence.
The manifest lists required assertion stages. The report uses the latest outcome for each stage.
A failure on one side of an exchange remains a failure when the other side passes.
After a failure, block only dependent branches. Continue independent work when the UI state is known.

## Pause and finish

Create `PAUSE` to stop dispatch. The existing recorder also interrupts an active CLI process.
An already dispatched operation can still complete. Inspect the checkpoint and fixtures before explicit resume.
Never remove `PAUSE` without explicit resume. Never retry a mutation solely because its completion event is missing.

Run `Export-MailAuditReport $run` to produce coverage, report, and retained-fixture files.
Add `findings.md` with severity, reproduction steps, expected/actual behavior, provider, and evidence links.
Record restored settings and unresolved restoration in `cleanup.md`.
Keep all messages in their final folders, including Trash. Preserve raw evidence.
Do not copy old outcomes into a new run. A blocked run is not a successful audit.

## Helper checks

```powershell
pwsh -NoProfile -File tests/scripts/Wino-MailAudit.Tests.ps1
pwsh -NoProfile -File tests/scripts/Wino-Harness.Tests.ps1
pwsh -NoProfile -File scripts/ui-audit/Test-Recorder.ps1
```

These checks validate tooling. They do not prove live Mail behavior.

## Focused live helpers

After guided setup and verified sends, these commands inspect the recorded reply fixtures:

```powershell
./scripts/ui-audit/Invoke-WinoMailThreadChecks.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailThreadStateChecks.ps1 -RunDirectory $run -Window $currentHwnd
```

The first helper checks both accounts. The second changes read and flag states on the replier's thread.
Both require fresh window discovery and reviewed fixture records. Neither sends messages.
`New-MailAuditCompose` fills a new draft. `Complete-MailAuditReply` validates and sends a prepared reply once.
Use these helpers only within the scenario contract. They do not replace preflight or guided recovery.

Run draft creation only for accounts without an existing `DRAFT-001` fixture:

```powershell
./scripts/ui-audit/Invoke-WinoMailDraftAttachmentChecks.ps1 -RunDirectory $run -Window $currentHwnd -Accounts $accountNames
./scripts/ui-audit/Invoke-WinoMailAttachmentReceipts.ps1 -RunDirectory $run -Window $currentHwnd
```

The first helper creates, reopens, edits, attaches, and sends once. A failure stops its batch for guided reconciliation.
The second checks existing fixtures without sending. It records each account's failure and continues independent accounts.
Review receipt screenshots for the sender address, body, attachment name, and account selection.
Pass the scenario manifest to `Test-MailAuditDependencies` to require all defined assertion stages.
Read-only UI commands retry one stale-element error. Input and mutation commands never retry automatically.

Attachment opening can launch the configured Windows image viewer. Inspect that viewer through recorded WinApp commands.
Use its File info UI to identify a downloaded cache path before comparing hashes.
Record external-viewer evidence separately from Mail evidence, including its HWND and theme.
If Windows refuses foreground input, stop input-dependent scenarios and request manual window activation.
Do not substitute unrecorded input, direct application APIs, or invented passes.

### Dedicated fixtures and directed sends

Run each send helper only after you inspect the current fixture ledger and composer.
The send helpers refuse existing fixtures. Reconcile an interrupted draft or send through the UI before continuing.
Receipt helpers inspect existing messages and never send them again.

```powershell
./scripts/ui-audit/Invoke-WinoMailDiscardChecks.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailFixtureSends.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailFixtureReceipts.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailDeletionReplies.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailSingleDeletionChecks.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailThreadDeletionChecks.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailForwardSends.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailFixtureReceipts.ps1 -RunDirectory $run -Window $currentHwnd -Purposes forward
./scripts/ui-audit/Invoke-WinoMailReplyAllSends.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailFixtureReceipts.ps1 -RunDirectory $run -Window $currentHwnd -Purposes reply-all
```

Forward checks use two configured recipients. Reply-all checks verify the sender and the third participant before sending.
Recipient validation includes To, Cc, and Bcc. An unexpected recipient blocks the send.
Deletion helpers retain their messages in Trash. They do not permanently delete messages.
After a failed deletion, inspect both folders before another mutation. Delayed removal must remain in the findings.

### Attachment, shortcut, and synchronization checks

```powershell
./scripts/ui-audit/Invoke-WinoMailAttachmentOpenChecks.ps1 -RunDirectory $run -Window $currentHwnd -Accounts $accountNames
./scripts/ui-audit/Invoke-WinoMailKeyboardStateChecks.ps1 -RunDirectory $run -Window $currentHwnd
./scripts/ui-audit/Invoke-WinoMailPostSyncChecks.ps1 -RunDirectory $run -Window $currentHwnd
```

Review the attachment screenshots after each opening check. Hash comparison checks the downloaded file, not the visual layout.
Keyboard checks record the initial state and verify both transitions. They do not retry uncertain key dispatch.
Run synchronization checks after reader checks. Opening a message can automatically mark it read.
The synchronization helper retains an unread, flagged move fixture for each account in `retained-state.jsonl`.
Run it after move checks return those fixtures to their recorded folders.
