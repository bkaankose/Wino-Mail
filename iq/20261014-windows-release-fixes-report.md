# Windows Release Fixes — report (14 Oct 2026)

Plan: `iq/20261014-windows-release-fixes.md`. All changes are local and uncommitted.

## Verification run on Windows
- `scripts/maintenance/windows/format-xaml.ps1 -Changed` then `-Check`: 23 of 23 files pass.
- Debug: `dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Debug -p:Platform=x64` — 0 warnings, 0 errors.
- Release gate: `dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` — 0 warnings, 0 errors.
- Tests (`dotnet test tests/<project> -p:MailCalTargetPlatform=Windows`):
  - MailCal.Core.Tests 1067/1068. The one failure, `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce`, also fails on a clean checkout of HEAD `80ca7f38`; it is pre-existing and unrelated.
  - MailCal.Mail.ViewModels.Tests 157/157.
  - MailCal.Mail.Controls.Tests 34/34.
  - Six new tests were added: the draft-missing save path, primary-alias repair, and the single-flight database initialization.

## Not run
- **Manual Release-package checks:** none performed. The checklists per area are in the plan and in the agent notes below.
- **macOS and iOS builds:** handoff to the Mac. The shared interface changes need them:
  - `IAccountService.EnsurePrimaryAccountAliasAsync` (new method).
  - `IMailService.SaveDraftMetadataAsync` now returns `Task<bool>`.
  - `SettingsShellPageMenuItem` has a new optional parent parameter.

## Per issue
| Issues | Status | Change |
|---|---|---|
| 2F2 2HA 2TN 2PR 2TZ | Fixed | `CalendarService.DeleteAccountCalendarAsync` SQL referenced a nonexistent `CalendarItem.AccountId`. |
| 2ZT | Fixed | `AccountService` hoists `Nullable<Guid>.Value` out of the sqlite-net lambda. The audit found and fixed one more site: `ContactService.SuppressContactPictureAsync`. |
| 2PG | Fixed | `GmailSynchronizer` handles a null People profile. |
| 2VD 31D 31E | Fixed | `MailService.CreateAssignmentInternalAsync` handles a null `AssignedFolder`. |
| 33W | Fixed | `ApplicationRuntime.StopCoreAsync` tolerates an uninitialized synchronization manager. |
| 2Q5 2Y5 2ZP 2Z4 33T 2W6 | Fixed (needs runtime check) | `MailCalAppShell` applies NavigationView selection from code, deferring nested items until the group is expanded and realized, with a guarded retry. `SettingsMenuProvider` clears selection before rebuilding. Residual risk: the mail-menu rebuilds still clear items under a live selection. |
| 2WZ 2WY | Likely already fixed | HEAD binds the concrete `AliasManagementItemList`. The 2.2.2.0 build predates `d44e011f`. Verify in the Release package. |
| 2HS | Fixed | `MailCategoryColorOptionList` replaces the array `ItemsSource`. |
| 2DD 33S | Defensive | Dialogs are given a XamlRoot only when they have none, taken from the active window. The AI features consent dialogs are guarded. The root cause was not located (no managed frames). |
| 31T | Fixed | `IsPaneOpen` is applied from code on the shell's own dispatcher, detached on close. |
| 2ZH 328 339 346 33B 34N 2R0 2E4 34E | Fixed | The editor and renderer convert "WebView2 gone" into `ObjectDisposedException`. Pages catch it during teardown and idle. Editor start-up failure shows a status message instead of crashing. |
| 2VW | Fixed | `SaveDraftMetadataAsync` and `DraftSaveService` return false when the draft is gone. `ComposePage.OnNavigatingFrom` catches the save failure. |
| 305 30J | Fixed | COM-safe activation reads. The migration stores a plain-data activation snapshot instead of the COM `AppActivationArguments`. |
| 30C 33V | Fixed | Migration dialog `COMException` is guarded. Launch cancellation during exit is handled. |
| 2NE | Fixed | Tray add failure no longer throws. It retries up to 5 times and again on `TaskbarCreated`. |
| 332 34B (and 2VY 333) | Fixed | Root cause: no theme defined these keys in its High Contrast dictionary. The keys were added to all 22 built-in themes (`AppThemes/*.xaml`), with a code fallback in `NewThemeService.EnsureThemeResources`. |
| 2VQ | Fixed | Accounts with no alias get a repaired primary alias (`EnsurePrimaryAccountAliasAsync`). The shell shows an InfoBar if repair is impossible. New string: `Info_DraftMissingAliasMessage`. |
| 34M 34K 33P 330 | Fixed | `DatabaseService.InitializeAsync` is single-flight and never replaces the connection. The likely trigger was `DatabaseMigrationCoordinator` side connections sharing sqlite-net's pooled handle and closing the live connection. They now use distinct open flags. |
| 34C | Fixed | `ToDoPageViewModel` reload loop stops or catches during shutdown. |
| 30M | Fixed | Menu recreation is skipped before mail mode has a menu. |
| 2WP | Fixed | Briefing panel skips UI work when it was never opened. |
| 2TE 2XR | Fixed | Expected Store HRESULTs are logged as warnings and not captured. |
| 2ZY 34F 33E 34D | Fixed | An attachment-policy open failure falls back to the normal launcher. Known HRESULTs are not captured. Real policy blocks are still honoured. |
| 313 | Not fixed | NullReferenceException inside the .NET COM wrapper, with no app frames. A CsWinRT or WindowsAppSDK bump needs to be evaluated separately. |
| 2C9 | Not fixed | No frames. Expected to be covered by the activation and theme fixes; re-check after release. |

## Follow-ups
- **`Program.cs:48`** still calls `GetActivatedEventArgs()` unguarded.
- **Share-target activations during migration** cannot be replayed after the migration completes.
- **`MailCalMailEditor.ReportInitializationFailure`** is a new public API; the controls playground was not updated.
- **Other draft-creating view models** (`MailListPageViewModel`, `MailRenderingPageViewModel`, `DailyBriefingPanelViewModel`) rely on the alias auto-repair but do not catch `MissingAliasException`.
