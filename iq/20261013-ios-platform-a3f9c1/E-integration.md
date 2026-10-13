# E: integration verification of Phase 0 (A + B + C + D)

Tree: `D:\Wino-Mail`, branch `feature/iOS`, HEAD `80ca7f38`, with the integrated changes staged (179 files). Nothing is committed.

Result: the integrated tree builds and tests clean on Windows and on the Mac (Debug arm64). **No fixes were needed.** No source files were edited.

## Windows (logs in `artifacts/ios-platform/T8-*.log`)

| Command | Outcome |
| --- | --- |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64` | Succeeded, 0 errors, 0 warnings (`T8-win-debug.log`) |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | Succeeded, 0 errors, 0 warnings (`T8-win-release.log`) |
| `dotnet build controls/Wino.Presentation.Core/Wino.Presentation.Core.csproj -c Release -p:Platform=x64` | Succeeded, 0 errors, 0 warnings (`T8-presentation-core-release.log`) |
| `dotnet test tests/Wino.Core.Tests -c Debug -p:Platform=x64` | 1108 passed, 1 failed, out of 1109. The failure is the known `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce`. |
| `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug -p:Platform=x64` | 186/186 passed |
| `dotnet test tests/Wino.Mail.Controls.Tests -c Debug -p:Platform=x64` | 58/58 passed |
| `dotnet test tests/Wino.Core.ML.Tests -c Debug -p:Platform=x64` | 10/10 passed |

The earlier crash left no stale `bin/obj` problems.

## Mac (`burak-mac`, mirror `~/Desktop/wt-main`; logs copied to `artifacts/ios-platform/T8-mac-*.log`)

The tree was mirrored with the tar pipe from the brief. The two `.xcframework` folders (Sparkle and WinoStoreKit2) were replaced with copies from `~/Desktop/Wino-Mail`.

| Command | Outcome |
| --- | --- |
| `~/.dotnet/dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true -p:CodesignKey=- -p:CodesignProvision=` | Succeeded, 0 errors, 54 warnings (`T8-mac-debug-arm64.log`). This matches B's and D's counts. Each warning is on a source line that is identical at HEAD (checked line by line), so none is new. |
| `~/.dotnet/dotnet build WinoMail.MacOS.slnx -c Debug -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true -p:CodesignKey=- -p:CodesignProvision=` | Succeeded, 0 errors, 3 warnings (`T8-mac-slnx-debug.log`). All 21 projects built. The 3 warnings are xUnit2031 in the unchanged `tests/Wino.Core.Tests/Services/KeyboardShortcutPlatformDefaultsTests.cs`. |
| `~/.dotnet/dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug` | 186/186 passed (`T8-mac-tests.log`) |
| `~/.dotnet/dotnet test tests/Wino.Mail.Controls.Tests -c Debug` | 58/58 passed |

Mirror note for later runs: the generic excludes do not skip `src/Wino.Mail.WinUI/AppPackages` (3.2 GB). It made the first mirror take about 12 minutes. I deleted it from `~/Desktop/wt-main` afterwards (the mirror is now 142 MB). Add `--exclude=AppPackages` to future mirrors.

Not run: the macOS x64 build, macOS Release builds, and an iOS build (none were requested).

## A/B abstraction overlap (recorded, not redesigned)

The two abstractions already coexist without changes:
- **A:** `NavigationCoordinator` (shared, `Wino.Mail.ViewModels/Navigation`) owns routes, history, back confirmation, `EnsureShellAsync`, `IsShellReady` and the account lifecycle reactions. A head implements `INavigationHost`; on the Mac that is `AppKitNavigationService`, which forwards `INavigationService` to the coordinator.
- **B:** `IApplicationShellHost` (shared, `Wino.Mail.ViewModels/Activation`) has `IsShellActive`, `EnsureShellAsync`, `ShowSettingsAsync` and `OpenMailFileAsync`. `ActivationRouter` and `NotificationResponseNavigator` use it. On the Mac, `AppKitApplicationShellHost` resolves `AppKitNavigationService` and delegates `EnsureShellAsync` to it, so the call goes through A's coordinator. `IsShellActive` reads `IsShellReady` and `Shell.ActiveMode`.
- **The overlap:** `EnsureShellAsync` and the shell-readiness check exist on both contracts. A new head (UIKit) would implement `INavigationHost` and `IApplicationShellHost`, and also `IApplicationLifecycleHost` (B's `StopNavigationAsync` is another navigation-lifetime hook). Only `ShowSettingsAsync` and `OpenMailFileAsync` are really head-specific.
- **Cleanup proposal (later):** add a shared `IApplicationShellHost` implementation over `NavigationCoordinator`. This needs the active application mode on `INavigationHost` or the coordinator. Heads would then supply only the settings presenter and the mail-file opener, perhaps as two small seams. Fold `StopNavigationAsync` into coordinator disposal.

## Consolidated manual runtime checks (pending; none performed)

**Mac**
1. Launch with accounts (opens the shell) and without accounts (opens Welcome). Onboarding leads to the shell.
2. Settings › Add account in a secondary window: back, close by the user, and an account created there. Quit is blocked during account setup.
3. Removing the last account returns to Welcome and closes the auxiliary windows.
4. Back confirmation on the contact and event editors.
5. Quit: Companion, shortcuts, reminders and navigation stop, and nothing hangs.
6. Activation through `EnsureShellAsync`: Dock, mailto, webcal and billing URLs; Finder `.ics`, `.vcf` and `.eml` files; Dock drop or Services-menu file creates a new mail.
7. Notification actions: tap a mail, tap a calendar item, Join, Snooze, Archive and Delete; Reply, Reply all and Forward drafts; inline reply send; inline reply fallback without a Sent folder. A tap on a cold launch is queued before services start.
8. Presentation (C):
   - Theme gallery and backdrops.
   - Mail list rows and heights, reader date, folder and mail menus, filter editor.
   - Calendar week and month layout, drag refusal, event details date and recurrence, mini-calendar.
   - Avatars and avatar colours, ToDo list colours.
   - Wino Account sign-in errors.
9. Settings pages that use `AnyPropertyBinding`: Intelligence coverage and management.
10. Gmail sign-in (the receiver now comes from a DI factory) and the default signature text.

**Windows**
1. Theme gallery, folder and filter glyphs, birthday, category text colour.
2. Gmail sign-in and the default signature text.

**Builds not covered**
- macOS x64 Debug.
- macOS Release arm64 and x64.

## Risks carried forward from the reports
- The `Wino.Presentation.Bindings` namespace has the same name as the view controllers' `Bindings` property. It compiles today, but bare `Bindings` inside `Wino.Presentation.*` could become ambiguous (D).
- C's `TimedCalendarLayout` swap on WinUI is not mechanical (C, follow-up).
- Hosts that call `SynchronizeOnceAsync` must have registered everything `InitializeAsync` needs (D).
