# Assignment B: notification responses, activation router, lifecycle order (Phase 0 items 3, 4, 5)

Worktree: `D:\Wino-Mail\.claude\worktrees\agent-a9e983be0b14cb15c` (branch `worktree-agent-a9e983be0b14cb15c`, base `80ca7f38`). Nothing is committed.

## Changed files

New:
- `src/Wino.Core.Domain/Models/Notifications/NotificationResponse.cs`: `NotificationResponse` record and `NotificationResponseKind` enum
- `src/Wino.Core.Domain/Models/Notifications/NotificationActionIdentifiers.cs`: action ids (`wino.open`, `wino.dismiss`, `wino.calendar.joinonline`, `wino.calendar.snooze`, `wino.mail.<Op>`), `SupportedMailActions`, `TryParseMailAction`, `FallbackSnoozeMinutes`
- `src/Wino.Core.Domain/Interfaces/INotificationResponseService.cs`
- `src/Wino.Core.Domain/Interfaces/INotificationResponseNavigator.cs`
- `src/Wino.Services/Notifications/NotificationResponseService.cs`
- `src/Wino.Mail.ViewModels/Activation/IApplicationShellHost.cs`
- `src/Wino.Mail.ViewModels/Activation/NotificationResponseNavigator.cs`
- `src/Wino.Mail.ViewModels/Activation/ActivationRouter.cs`: also `IActivationFile` and `ActivationItem`
- `src/Wino.Mail.ViewModels/Lifecycle/ApplicationLifecycleCoordinator.cs`: also `IApplicationLifecycleHook`, `IApplicationLifecycleHost` and `ApplicationStartupTarget`
- `src/Wino.Mail.MacOS/Infrastructure/AppKitApplicationShellHost.cs`
- `tests/Wino.Core.Tests/Services/NotificationResponseServiceTests.cs` (27 tests)
- `tests/Wino.Mail.ViewModels.Tests/ActivationRouterTests.cs` (11 tests)
- `tests/Wino.Mail.ViewModels.Tests/ApplicationLifecycleCoordinatorTests.cs` (5 tests)

Modified:
- `src/Wino.Services/ServicesContainerSetup.cs`: one registration
- `src/Wino.Platform.MacOS/Services/MacNotificationCategories.cs`: the identifiers and parse helpers now forward to `NotificationActionIdentifiers`. The string values are unchanged.
- `src/Wino.Mail.MacOS/Infrastructure/MacNotificationResponseHandler.cs`: now a thin delegate
- `src/Wino.Mail.MacOS/Infrastructure/MacActivationCoordinator.cs`: now a thin NSUrl adapter
- `src/Wino.Mail.MacOS/AppDelegate.cs`: `StartAsync` and `QuitAsync` call the coordinator
- `src/Wino.Mail.MacOS/Infrastructure/MacViewModelRegistration.Activation.cs`, `MacViewModelRegistration.Notifications.cs`

None of the excluded files were touched (WinUI, `AppKitNavigationService`, main `MacViewModelRegistration.cs`, `Composition.cs`, `controls/**`, `src/Wino.Core/**`, `WinoViewModelRegistration.cs`).

## DI registrations

- Shared (`RegisterSharedServices`): `INotificationResponseService` -> `NotificationResponseService` (transient). It needs `INotificationResponseNavigator`, which each head registers. WinUI never resolves the service today, so WinUI has no new requirement.
- Mac `RegisterActivationServices`: `IApplicationShellHost` -> `AppKitApplicationShellHost`, `ActivationRouter`, `MacActivationCoordinator`, `ApplicationLifecycleCoordinator` (all singletons).
- Mac `RegisterNotificationServices`: `INotificationResponseNavigator` -> `NotificationResponseNavigator` (singleton).

## What moved and what stayed

T3:
- Moved to `NotificationResponseService` (Wino.Services):
  - dismiss handling, and calendar Join, Snooze and Open
  - snooze fallback chain (`ResolveSnoozeMinutes`, public static): offered value, then preference, then 5
  - mail Open with the missing-mail fallback
  - mail actions through `IWinoRequestDelegator`, followed by a badge update
  - Reply, Reply all and Forward draft creation (requires a Drafts folder)
  - inline reply: alias resolution from the draft's From header, then the primary alias (`ResolveSendingAliasAsync`, internal); Sent-folder check; composer fallback that reuses the draft
  - mode routing (`ResolveMode`, public static)
  - public `OpenMailAsync`, `OpenCalendarItemAsync` and `JoinOnlineAsync`, which the companion and status item use
- UI steps go through `INotificationResponseNavigator`. Its implementation, `NotificationResponseNavigator` (ViewModels), covers:
  - a mail in its folder, using the `LaunchParameter` handoff when the mail shell is not active
  - the Drafts folder and the composer, on the UI thread through `IDispatcher`
- Stayed on Mac (`MacNotificationResponseHandler`):
  - the `UNUserNotificationCenterDelegate`: `WillPresent` options and the cold-launch queue (`Attach`/`Detach`)
  - translation from `UNNotificationResponse` to `NotificationResponse` (kind, action id, category, user text, user info values)
  - forwarders `NavigateMailAsync`, `NavigateCalendarItemAsync` and `JoinOnlineAsync`, so `AppDelegate.Companion.cs` is unchanged

T4:
- `ActivationRouter` (ViewModels) holds the neutral activation rules:
  - grouping (`Group`, public static): URLs in delivery order; calendar, contact and .eml file groups; unsupported files become attachments; unsupported URLs are ignored
  - the serializing gate
  - has-accounts gates
  - mailto with the `MailToUri` handoff, or `MailtoProtocolMessageRequested` when the mail shell is active
  - billing success -> Settings > Wino Account with `CheckoutCompleted`
  - webcal -> calendar
  - .ics and .vcf import with access scopes and the import-failed info bar
  - share as `PendingShareRequest`, or `HandlePendingShareRequestAsync` when the mail shell is active
  - .eml: distinct paths, at most 10 windows, a read inside the access scope, and the failure dialog
  - `AttachFilesAsync`
- `MacActivationCoordinator` keeps only:
  - NSUrl classification (`Classify`)
  - the `SecurityScopedFile : IActivationFile` wrapper (Start/StopAccessingSecurityScopedResource)
  - its public API, so `AppDelegate.Activation.cs` and `EmlReaderWindow` debug commands are unchanged
- `AppKitApplicationShellHost` covers `IsShellReady && ActiveMode`, `EnsureShellAsync`, the Settings window presenter (with a mode fallback) and `EmlReaderWindow.OpenAsync`.
- `ApplicationLifecycleCoordinator`:
  - Start order: `SetupLogger(<data>/Logs/wino.log)`, runtime start, theme init, `RuntimeStarted = true`, host `ServicesReadyAsync`, account check, then `ShowStartupAsync(Shell|Welcome)`.
  - Stop order: host `StoppingAsync(runtimeStarted)`, host `StopNavigationAsync`, then runtime stop only if it started.
  - The runtime, theme and account services are resolved after `SetupLogger`, as before. This keeps services that call `Log.ForContext` in their constructors from capturing the silent logger.
- `AppDelegate` implements `IApplicationLifecycleHost` explicitly:
  - `ServicesReadyAsync` sets `_runtimeStarted` and calls the six `*ServicesReady` partials in the original order, then the DEBUG bridge.
  - `ShowStartupAsync` dispatches `ShowShellAsync` or `NavigateAsync(WelcomePageV2)` to the UI thread, as before.
  - `StoppingAsync` calls Companion, then ShortcutMenus, then Notifications (only if the runtime started).
  - Menus, Dock, status item and Sparkle stay in the AppDelegate partials.
  - Composition and the container dispose stay in AppDelegate.

Intended behaviour changes: none. Minor differences:
- The "unsupported activation" log line now records the kind instead of the URL scheme.
- An inline reply passes `string.Empty` instead of `null` as `InitialBodyText`. `MailService` checks with `IsNullOrWhiteSpace`, so the effect is the same.

## Verification (Windows host, inside the worktree)

Logs are in `artifacts/ios-platform/B-*.log`, copied to `D:\Wino-Mail\artifacts\ios-platform\`.

| Command | Result |
| --- | --- |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64` | 0 errors, 0 warnings (`B-winui-debug-x64.log`) |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | 0 errors, 0 warnings (`B-winui-release-x64.log`) |
| `dotnet test tests/Wino.Core.Tests -c Debug -p:Platform=x64` | 1088 passed, 1 failed (`B-core-tests.log`) |
| `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug -p:Platform=x64` | 173 passed, 0 failed (`B-viewmodels-tests.log`) |
| Mac: `~/.dotnet/dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true -p:CodesignKey=- -p:CodesignProvision=` in `~/Desktop/wt-B` | 0 errors, 54 warnings (`B-mac-debug-arm64.log`) |

Notes on these results:
- **Core tests:** the one failure is the known `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce`. The 27 new tests pass. The total is 1089, against the stated baseline of 1043, but none of my code accounts for the extra tests.
- **ViewModels tests:** 157 existing plus 16 new. The 11 `WinoIntelligenceHeaderPresenterTests` failures listed in the baseline do not reproduce here; all 13 of those tests pass, and I did not touch that code.
- **Mac warnings:** all 54 are pre-existing warnings (CA1422 and similar). None are in the new or changed files.
- **Restart recovery:** after the PC restart, the WinUI `obj` folder was corrupted (NUL bytes in generated files). I deleted `bin`/`obj` under `src`, `controls` and `tests` in the worktree before rebuilding.
- **Mac mirror symlinks:** the tar mirror from a Windows checkout turns git symlinks into text files. That breaks `Sparkle.xcframework` and `WinoStoreKit2.xcframework` with MT7091. I restored the 9 symlinks (`git ls-files -s | grep ^120000`) inside `~/Desktop/wt-B` before building. Other agents mirroring from Windows will hit the same problem.

## Pending and risks

- Manual Mac runtime checks, not performed:
  - launch with accounts (shell) and without (Welcome)
  - quit (Companion, shortcuts, reminders and navigation stop; no hang)
  - notification actions: tap mail, tap calendar, Join, Snooze, Archive or Delete, Reply/Reply all/Forward drafts, inline reply send, inline reply fallback without a Sent folder
  - a cold-launch tap that is queued before services start
  - mailto, webcal and billing URLs
  - Finder .ics, .vcf and .eml files
  - a Dock drop or Services-menu file -> new mail
- `IApplicationShellHost` overlaps with the navigation coordinator from item 2 (another agent). Once `NavigationCoordinator` exists, `AppKitApplicationShellHost` can delegate to it. The interface does not need to change.
- `MacNotificationCategories.SupportedMailActions` changed from a field to a property that returns the shared array. No caller mutates it.

## WinUI adoption notes (not done in this item)

WinUI could later register a WinUI `IApplicationShellHost` and `INotificationResponseNavigator`, then route these handlers through the shared services:

- **`src/Wino.Mail.WinUI/App.xaml.cs`:**
  - `HandleToastActivationAsync` (1062, 1065)
  - `HandleCalendarToastNavigationAsync` (1291) -> `OpenCalendarItemAsync`
  - `HandleCalendarToastSnoozeAsync` (1332) -> snooze rule. Windows passes the snooze length through toast user input, which needs mapping to `Constants.ToastCalendarSnoozeDurationInputId`.
  - `HandleCalendarToastJoinOnlineAsync` (1343) -> `JoinOnlineAsync`
  - `HandleToastNavigationAsync` (1425) -> `OpenMailAsync`
  - `HandleToastActionAsync` (1503)
  - `HandleToastComposeActionAsync` (1590)
  - `HandleStoreUpdateToastAsync` (1276) stays Windows-only.
- **`src/Wino.Mail.WinUI/Activation/AppNotificationHandler.cs`:** `TryCreateActivationRoute` would build a `NotificationResponse`. Windows toast argument names differ from the Mac identifiers (for example `Constants.ToastCalendarJoinOnlineAction`), so they need mapping.
- **Activation:** `HandleMailToProtocolActivationAsync` (1106), `HandleShareTargetActivationAsync` (1068) and `HandleFileActivationAsync` (2256) could use `ActivationRouter`, with a no-op `IActivationFile.BeginAccess`.
