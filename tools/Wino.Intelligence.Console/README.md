# Wino Intelligence console

A terminal version of Wino for mail synchronization and Wino Intelligence indexing. It builds the
same service graph as the app (`RegisterCoreServices` and `RegisterSharedServices`) and runs it
against the app's real data in `%LOCALAPPDATA%\Packages\<package family>\LocalState`
(`Wino210.db`, `WinoMailIntelligence.db`, `OutlookCache.bin`).

Close Wino Mail first. The console checks the app's `MailHostRunning` mutex and exits when the app
is running, because both would write the same database and synchronization cursors.

```powershell
dotnet run --project tools/Wino.Intelligence.Console -p:Platform=x64
dotnet run --project tools/Wino.Intelligence.Console -p:Platform=x64 -- --api local --account you@example.com --scenario index
```

At startup the console asks for the API (local `https://localhost:7204/` or production), probes it,
prints the Wino account, add-on, quota and consent, and lists the mail accounts.

## Scenarios

| Key | What it does | App behaviour it mirrors |
| --- | --- | --- |
| `sync` | Runs a mail synchronization and prints per-folder counts and time. The synchronizer decides between initial and delta. | `App.HandleMailSynchronizationRequestedAsync` |
| `index` | Consent, add-on check, enable, coverage (inbox latest N), plan, submit, then follows the job and prints a phase timeline and result summary. | Intelligence management page, `StartIndexingCommand` |
| `delete` | Cancels jobs, deletes local results, turns indexing off. | `DeleteSemanticIndexAsync` |
| `revoke` | Revokes consent on the server and clears local intelligence for every account. | `SetIntelligenceConsentAsync(false)` |
| `status` | Wino account, add-on, quota, consent, mailbox id, device result key, local jobs, and server jobs classified as tracked, orphaned or another device's. | — |
| `analyze` | Sends one inbox message through `messages:analyze` and prints both artifacts. | Reader header process button |
| `poll` | Polls unfinished jobs once and can follow them to completion. Use after quitting mid-job. | Resume loop |
| `jobs` | Cancels all or one job, or re-polls one job. | `CancelAsync`, `CancelJobAsync`, `RetryJobAsync` |
| `artifacts` | Result summary, the newest processed messages, and the daily briefing cards. | Daily briefing |
| `mailboxes` | Exports the mailbox list (without preferences) so each account has a server mailbox id. | Account data sync export |
| `reindex` | `delete` then `index` with one total time. | — |
| `index-folder` | `index` for any selectable folder. Over 1000 messages splits into several jobs. | — |
| `consent` | Shows consent and accepts the current policy if needed. | Consent dialog |

Ctrl+C cancels the running scenario and returns to the menu. `--yes` answers every confirmation.

## Differences from the app

- Preferences start at their defaults, because the app's settings live in the package settings
  hive, which a process without package identity cannot read. Account preferences are in the
  database and are real.
- Consent is accepted after a console prompt instead of the policy dialog.
- The console sets `IsSemanticIndexingEnabled` in both directions. The management page only
  assigns it when enabling.
