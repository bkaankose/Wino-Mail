# Plan: Standalone Wino Mail fork (no cloud services) + calendar tray popup

## Context

The user has forked Wino Mail (GPL-3.0, upstream `bkaankose/Wino-Mail`) as `hairokuma/Mail-Cal`. The fork has to be a private, standalone mail and calendar client:

1. **No external services.** Remove AI ("Wino Intelligence", Daily Briefing, Magika ML), Wino Account / Wino Sync, Store purchases and update checks, Sentry telemetry, Gravatar and Google favicon lookups, and third-party mail autodiscovery. The only network traffic left is to the mail and calendar providers the user configures (IMAP/SMTP, Gmail API, Microsoft Graph, CalDAV/CardDAV, and their OAuth endpoints).
2. **Tray popup becomes a calendar.** Left-clicking the tray icon shows a month calendar. Below it is the list of all events on the selected day, and below that a quick-add form (title, all-day toggle, start and end time, calendar).
3. **Linux support later.** The framework is still undecided. Nothing is built for it now, but the work above must not make a later port harder.

Decisions already made with the user:
- **Drop the macOS head.**
- **Remove** Gravatar and favicon lookups, and mail autodiscovery.
- **Replace** the Companion flyout content; don't extend it.
- **Quick-add** uses a title, time pickers and an all-day toggle.

Working copy: `C:\Users\fdoelling_ailio\source\Wino-Mail` (Windows). It builds and runs with `winapp run`. All builds and verification happen there. The WSL clone is only for reading.

---

## Phase 0: Set up the fork

1. **The fork already exists** at https://github.com/hairokuma/Mail-Cal (a GitHub fork of `bkaankose/Wino-Mail`; `main` is at `d137c35`, the same commit as the local clone).
   - In `C:\Users\fdoelling_ailio\source\Wino-Mail`, rename the current `origin` to `upstream` and add `origin` = `https://github.com/hairokuma/Mail-Cal.git`.
   - Work on branch `standalone`. Push only when the user asks.
2. **Give the app its own package identity**, so it doesn't collide with the Store-installed Wino Mail Preview and doesn't look like the official app:
   - `src/Wino.Mail.WinUI/Package.appxmanifest`: Identity `Name` and `Publisher` (use a self-signed dev certificate subject), `DisplayName`, and `PublisherDisplayName`.
   - Update the matching values in `docs/harness/development-windows.md` and `scripts/development/windows/start-wino.ps1` if they hardcode `58272BurakKSE.WinoMailPreview`.
   - Keep the `wino:` protocol only if deep links still need it after Phase 2. Otherwise rename it.
3. **Remove the macOS head:**
   - Projects: `src/Wino.Mail.MacOS`, `src/Wino.Platform.MacOS`, `src/Wino.Core.MacOS.Bindings`, and `controls/*AppKit*`, `controls/Wino.Editor.AppKit`.
   - Solution and workspace files: `WinoMail.MacOS.slnx` and `WinoMail.MacOS.code-workspace`.
   - Build files: `build/Wino.MacOS.*`, plus the MacOS branches in `Directory.Build.props/targets`. Keep the `WinoTargetPlatform` mechanism itself, because Linux will plug into it later.
   - Scripts and docs: `scripts/*/macos`, macOS docs, and the macOS sections in `AGENTS.md`.
4. Delete the legacy projects `src/Wino.Core.ViewModels` and `src/Wino.Calendar.ViewModels`. Neither solution references them.
5. Update the README with what this fork is, credit to upstream, and the GPL-3.0 notice (unchanged).

## Phase 1: Telemetry and calling home

- **`src/Wino.Services/WinoLogger.cs`**
  - Remove `SentrySdk.Init`, the Sentry Serilog sink, and the scrubbers.
  - Keep the `IWinoLogger` interface and the local Serilog file logging.
  - `TrackEvent`, `CaptureException` and `UploadDiagnosticLogsAsync` have about 40 callers. Turn them into local log writes or no-ops first, then remove `UploadDiagnosticLogsAsync` from the interface and its callers.
- **Delete:**
  - `WinoSentryEventScrubber.cs`, `WinoTelemetrySanitizer.cs`, `WinoTelemetryContextProvider.cs` and `Models/Telemetry/*`
  - the `SentryDNS` member in `ApplicationConfiguration.cs:37` and on `IApplicationConfiguration`
  - the `Sentry*` members on `IAppMetadataService` / `NativeAppService.cs`
- **Remove log-upload buttons:** `AboutPageViewModel.cs:128-149` and `AccountDetailsPageViewModel.cs:568` (the IMAP log upload). Replace them with "Open log folder".
- **Remove packages:**
  - `Sentry.Serilog` from every csproj and from `Directory.Packages.props`
  - the unused `Serilog.Sinks.ApplicationInsights`
- **Release scripts:** remove `upload-sentry-symbols.ps1` and its hooks in `scripts/release/windows/*`.
- **Default the logging preference to local only.** Rename it from `IsLoggingEnabled` if it now means only file logging.

## Phase 2: Wino Account, Wino Sync, Store and purchases

- **Delete the services** in `src/Wino.Services`:
  - `WinoAccount{ApiClient,AvatarProcessor,DataSyncService,ExternalSignInService,IntelligenceSnapshotService,ProfileService,SessionService}`
  - `WinoAppStorePurchaseService`, `WinoBillingService`, `WinoPendingCheckoutStore`, `WinoPurchaseReconciliationService`, `WinoStorePurchaseRedeemService`
- **Delete the matching interfaces, models, enums, exceptions and translators** in `src/Wino.Core.Domain`. See the inventory: `IWinoAccount*`, `IWinoBillingService`, `IMicrosoftStoreService`, `IAppStoreClient`, `WinoAccount*`, `StorePurchaseResult`, and others.
- **Keep local file backup.**
  - Move `ExportToFileAsync` / `ImportFromFileAsync` out of `WinoAccountDataSyncService` into a new `LocalBackupService : ILocalBackupService`.
  - It keeps using `SyncSnapshotKeyService` and Argon2 for the file encryption.
  - Point `BackupRestorePageViewModel` and `WelcomePageV2ViewModel` at it. Remove their Export/ImportToWinoAccount paths and `ImportFromWinoAccountAsync`.
- **Remove the 3-account limit:**
  - `Constants.FreeAccountLimit` and its checks in `AccountManagementPageViewModelBase.cs:166` and `AccountManagementViewModel.cs:100`
  - `PurchaseUnlimitedAccountAsync`, `UnlimitedAccountsStorePurchase`, `AppStoreAddOnPurchase`, and the `WinoAddOn*` view models
- **Store service:**
  - Delete `src/Wino.Mail.WinUI/Services/MicrosoftStoreService.cs`, which does in-app purchases, rating prompts and update checks.
  - Remove the injections and uses in `WinoAppShellViewModel:270-311`, `MailAppShellViewModel:1016-1030,1257`, `AboutPageViewModel:164-179`, and `SettingOptionsPageViewModel:406`.
  - Remove the store-update toast code in `App.xaml.cs` (around 1368-1383, 1427-1439, 1535-1543, 2109-2136, 2247-2262 and 2501) and the `StoreUpdate` activation route.
  - Set the `PlatformCapabilities.MicrosoftStore` flag to false, or remove it.
- **Remove the WinUI UI:**
  - `Views/Settings/WinoAccountManagementPage*`
  - the `WinoAccount*Dialog*` files and `UnlimitedAccountsPurchaseChannelDialog*`
  - `App.WinoAccountAuthActivation.cs` and `App.BillingActivation.cs`
  - the auth-callback routes in `Activation/AppActivationRoutes.cs`
  - the route in `NavigationRouteTable.cs:76` and the entries in `SettingsMenuProvider`
  - `WindowsBrowserAuthBroker`, `WindowsExternalAuthCallbacks`, and the Wino Account secret methods in `WindowsAccountCredentialPersistence.cs`
- **DI:**
  - Remove the registrations in `src/Wino.Services/ServicesContainerSetup.cs` and `src/Wino.Mail.WinUI/CoreUWPContainerSetup.cs:49,65-80`.
  - The `Lazy<>` cycle breakers become unnecessary; delete them.
  - Also remove the ViewModel registrations in `App.xaml.cs:777-794`.
- **Messages:** delete the `WinoAccount*Message` types.
- **Database:**
  - Leave the `WinoAccount` table in `Wino210.db` alone. Dropping it is a pointless migration risk.
  - Remove it only from `DatabaseService` table creation for new installs, if that is clean to do. Otherwise leave it.
- **`AppUrls.cs`:**
  - Point the website, privacy and GitHub links at the fork.
  - Remove the Discord, PayPal and account-deletion links.

## Phase 3: AI and intelligence

- **Projects:** delete `src/Wino.Core.ML` (the Magika ONNX model and NativeRuntime DLLs) and `tools/Wino.Intelligence.Console`, and remove both from `WinoMail.slnx`.
- **Attachment type detection:** replace it with a stub `IContentTypeDetectionService` that always returns `ContentTypeDetectionResult.Unavailable`, so the existing extension and MIME fallbacks apply. That path is already handled.
- **Packages:** remove `Microsoft.ML.OnnxRuntime`, `Wino.Mail.AI.Abstractions`, `Wino.Mail.AI.ContentProcessing` and `Wino.Mail.Contracts` from `Directory.Packages.props` and from the Core.Domain, Core, Services and WinUI csproj files.
  - Check first whether `IMailContentProjector` (from `AI.ContentProcessing`) is used outside intelligence. If it is, move the minimal logic in-tree.
- **Domain, Services and ViewModels:** delete every intelligence type listed in the inventory:
  - interfaces: `IIntelligence*`, `IMailIntelligence*`, `IWinoIntelligenceCoordinator`, `ISemanticMailBodySynchronizer`
  - `Entities/Intelligence`, `Models/Intelligence|Ai|SemanticIndexing`, `DailyBriefingTone`
  - `CloudIntelligenceBackend`, the `MailIntelligence*`, `IntelligenceResultKey*` and `WinoIntelligence*` classes, and `LocalizedAiRequests`
  - the DailyBriefing and Intelligence view models
- **Synchronizers:** remove the `ISemanticMailBody*` implementations:
  - `GmailSynchronizer.cs:93, 3197-3290`
  - `ImapSynchronizer.cs:50, 544-590`
  - `OutlookSynchronizer.cs:85, 3759-3860`
- **Core services:**
  - Remove the optional intelligence parameters and calls in `MailService` (`HydrateIntelligenceMetadataAsync`) and `AccountService`.
  - Remove initialization steps 6 and 7 and the intelligence store from `ApplicationRuntimeInitialization.cs` and `CoreContainerSetup.cs:35-45`.
- **Mail list:**
  - Remove `IntelligenceTiles` from `MailItemViewModel.cs:77-104,235-276`. `RowTiles` should contain only categories.
  - Remove the intelligence parts of `MailListPageViewModel`.
  - Remove the intelligence tiles in `Styles/Mail/MailListItemTemplates.xaml:49-127`.
  - Remove `MailCopy.IntelligenceMetadata`, but only if it isn't a persisted column. If it is, leave the column in place.
- **WinUI UI:**
  - Daily Briefing: the toggle and panel in `ShellWindow.xaml:179-198,468`.
  - Mail reader: `WinoIntelligenceHeader` in `MailRenderingPage.xaml:567+` and its code-behind.
  - Compose: `RewriteSession` in `ComposePageViewModel` and `ComposePage.xaml`.
  - Pages: `WinoIntelligencePage`, `WinoIntelligenceManagementPage` and `IntelligenceCoveragePage`.
  - Dialogs and helpers: `IntelligenceConsentPolicyDialog` and `WinoAccountAiErrorTranslator`.
  - Navigation: the routes in `NavigationRouteTable.cs:77-79` and the menu entries in `SettingsMenuProvider.cs:24,170-215,310`.
  - **Fix `MailAppShellViewModel.cs:1752`**, which assumes "New Mail + Daily Briefing" are the first two menu items.
- **Controls:**
  - `controls/Wino.Mail.Controls.WinUI/{IntelligenceHeader,IntelligenceProgressRing,IntelligenceTileBar}` and `Themes/WinoIntelligenceBranding.xaml` (also remove its reference at `App.xaml:16`)
  - `controls/Wino.Mail.Controls.Core/{IntelligenceHeader,IntelligenceTileBar}`
  - the playground pages for them
- **Messages:** delete `DailyBriefingStateChanged` and the `Intelligence*` / `WinoIntelligence*` messages in `src/Wino.Messages/UI/`.
- **Database:** leave the intelligence columns in `MailAccountPreferences` (lowest risk). Delete the `WinoMailIntelligence.db` file on first start, if it exists.
- **Docs:** delete `docs/mail-intelligence.md` and update `AGENTS.md`.

## Phase 4: Third-party lookups

- **`src/Wino.Services/ThumbnailService.cs`:**
  - Remove the Gravatar call (`:213`) and the Google favicon call (`:243`).
  - Keep local and provider-supplied contact photos, and initials.
  - Remove the `IsGravatarEnabled` / `IsFaviconEnabled` preferences and their settings UI.
  - Delete the duplicate `src/Wino.Mail.WinUI/Services/ThumbnailService.cs`.
  - The `Thumbnail` cache table stays.
- **`src/Wino.Core/Services/AutoDiscoveryService.cs`:**
  - Remove the Thunderbird ISPDB lookup, Firetrust, and the dns.google DNS-over-HTTPS calls, including the MX- and SRV-based lookups that depend on them.
  - Keep probes that only contact the user's own domain: `autoconfig.<domain>` XML and the guessed host names checked with system DNS (`Dns.GetHostAddressesAsync`).
  - For CalDAV/CardDAV, keep only the `/.well-known/caldav|carddav` probes on the user's domain.
  - If this turns out to be too little, the manual IMAP/SMTP server entry stays available as the fallback.
- **`ImapClientPool.cs:679`:** remove the IMAP ID `SupportUrl = winomail.app`. Keep only the client name and version, renamed for the fork.
- **OAuth client IDs** in `src/Wino.Authentication/MailAuthenticatorConfiguration.cs:18-19`: both belong to upstream's registered apps. They keep working, but for a real standalone fork the user should register their own Azure app and Google OAuth client. Note this in the README as a follow-up. Don't change it now.

## Phase 5: Calendar tray popup (replaces the Companion content)

**Status (2026-10-23):** implemented, uncommitted; Debug x64 and the Release x64 gate pass. Manual checks in the running app are pending (see Verification, Tray). Deviations from the steps below:
- Rows are `CompanionDayEvent` (time text for the selected day, colour, location) and the calendar picker uses `CompanionCalendarOption`, rather than binding `CalendarItemViewModel` / `AccountCalendar` directly.
- "Shown in the calendar page" is read from `AccountCalendar.IsExtended`, which the calendar page's checkbox persists. The quick-add list holds the shown calendars that are writable.
- `CalendarOperationPreparationRequest` has no `Trace`, so quick-add events carry no `RequestTrace`.
- "More options…" passes the whole form (calendar, title, times, all-day) to the editor through `CompanionEventDraft`, instead of `NewEvent(accountId, startAt)`.
- The month calendar is a plain `CalendarView`; busy-day dots were not added.
- Unused companion strings were removed from `en_US` only; other locales are untouched.
- `TranslationService` now loads English first and lays the selected language over it. Before, a key missing from the selected language showed as its raw name (for example `Companion_QuickAddPlaceholder` in German), which made the "English strings only" rule unusable.

The tray icon (`Services/NativeTrayIcon.cs`, custom Win32), its controller (`Services/MainTrayController.cs`) and the flyout host (`ThirdParty/DesktopFlyouts/CompanionFlyoutHost.cs`, 400px wide, at most 720px tall) all stay as they are. Left-click opens the popup; right-click keeps the native menu (Show Mail-Cal, Show Calendar, Exit). Only the content and the view model change.

### ViewModel

Rework `src/MailCal.Mail.ViewModels/Companion/CompanionDashboardViewModel.cs`. It's free to change now that macOS is gone.
- **Remove:** greeting, snooze, next-event card, unread mail, tasks and favourites.
- **`SelectedDate` (DateOnly or DateTime, default today):** changing it reloads `DayEvents`.
- **`DayEvents : ObservableCollection<CalendarItemViewModel>`** (concrete type, for the AOT rule `MCAOT001`):
  - Base it on `LoadEventsAsync` (line 440).
  - Loop accounts, then `ICalendarService.GetAccountCalendarsAsync`, using every calendar the user shows in the calendar page (the same rule as `CalendarPageViewModel.LoadCalendarItemsAsync`, line 1034), not only primary calendars.
  - Call `GetCalendarEventsAsync(calendar, TimeRange(selected day 00:00 to next day 00:00))` for each.
  - Remove duplicates by Id and include all-day events.
  - Sort all-day events first, then by start, end and Id.
  - Keep the "skip declined" filter.
- **Quick-add state:**
  - `QuickTitle`, `QuickIsAllDay`
  - `QuickStart` / `QuickEnd` (TimeSpan): defaults are the next full hour and one hour after it; for a day other than today, 09:00–10:00.
  - `WritableCalendars : ObservableCollection<AccountCalendar>` and `SelectedQuickCalendar`. The default comes from the same rule as `CalendarPageViewModel.EnsureSelectedQuickEventAccountCalendar()`: the first `IsPrimary` writable calendar, otherwise the first writable one.
- **Commands:**
  - `CreateQuickEventCommand`: enabled when the title isn't empty and end is after start. On success it clears the title; the list updates through the existing `CalendarItemAdded` message.
  - `OpenEventCommand`: uses the existing `CompanionActionHandler.OpenCalendarEventAsync`, then `App.HandleCalendarToastNavigationAsync`.
  - `OpenFullEditorCommand`: uses the existing `NewEvent(accountId, startAt)` callback, then `App.OpenCompanionNewEventAsync`.
  - `OpenCalendarCommand`.
- **Live refresh:** keep `RegisterRecipients` and its debounce for `CalendarItemAdded`, `CalendarItemUpdated` and `CalendarItemDeleted`.

### Event creation

There should be one implementation, not two.
- Extract the request-building code from `CalendarPageViewModel.SaveQuickEventAsync` (around line 590) into a shared helper, for example `Calendar/QuickEventFactory.cs` in `MailCal.Mail.ViewModels`.
- The helper builds a `CalendarEventComposeResult` (`TimeZoneId = TimeZoneInfo.Local.Id`, `ShowAs = calendar.DefaultShowAs`, empty attendees, reminders and recurrence), validates it with `CalendarEventComposeResultValidator`, and dispatches `CalendarOperationPreparationRequest(CalendarSynchronizerOperation.CreateEvent, ...)` through `IMailCalRequestDelegator`.
- `CalendarPageViewModel` and the new `CompanionActionHandler.CreateQuickEventAsync` both call it. Follow the pattern of `CreateMyDayTaskAsync` there, including `RequestTrace("tray-companion", ...)`.
- `MailCalRequestDelegator` already refuses read-only calendars and queues the sync.

### View

Rewrite `src/MailCal.Mail.WinUI/Controls/Companion/CompanionFlyoutView.xaml` and its `.xaml.cs` (about 825 + 197 lines).
- **Month calendar:** reuse `Controls/ShellCalendarView` (or a `CalendarView` with a style based on `MailCalCalendarViewStyle` from `Styles/CalendarViewStyles.xaml`) with `SelectionMode="Single"`. A "Today" button sits in the header.
  - Optional nice-to-have: dots on days that have events, done in `CalendarViewDayItemChanging` against a per-month set of busy days the ViewModel caches.
- **Day header and event list:**
  - A header for the selected day ("Wednesday, 22 October"), then a scrollable `ItemsControl`/`ListView` over `DayEvents`.
  - Each row shows the calendar colour dot, the time range (or "All day"), the title and the location.
  - Reuse the existing event-card and "later events" templates (about lines 300–466) as the starting point.
  - Show an empty state such as "No events".
- **Quick-add form:**
  - A TextBox (Enter submits), an all-day ToggleSwitch or CheckBox, two `TimePicker`s that hide when all-day is on, a calendar `ComboBox` that shows the colour dot and the calendar/account name, an Add button, and a "More options…" link that opens the full editor.
- **Rules to follow:**
  - Use `x:Bind` only. Casts to WinRT types need `[DynamicWindowsRuntimeCast]`. No `ContentDialog` in the popup.
  - Add new strings only to `src/MailCal.Core.Domain/Translations/en_US/resources.json`.
  - Remove the Companion strings that are no longer used.
- **Risk to check early:** `TimePicker` and `ComboBox` open their own popups inside `DesktopWindowXamlSource`. Confirm the flyout's hide-on-focus-loss in `CompanionFlyoutHost` doesn't close the window while they're open. The existing snooze split-button menu suggests popups work. If they don't, suppress hiding while a child popup is open, as the existing popups already do.
- **Size:** keep the width at 400. The event list takes the remaining height, up to the existing 720px maximum, and scrolls.

### Settings and wiring

- **Settings:**
  - `CompanionSettingsPage` / `CompanionSettingsPageViewModel`: remove `ShowCalendarInCompanion`, `ShowUnreadMailInCompanion`, `ShowTasksInCompanion` and `ShowFavoriteContactsInCompanion`.
  - Keep `IsCompanionEnabled` and the hotkey settings.
- **Unchanged:** `Services/Companion/CompanionService.cs`, which creates the ViewModel and view at line 207 and hides the popup after `NavigationCompleted`. Only adjust the constructor arguments.
- **Startup:** `App.xaml.cs` `EnsureTrayIconCreated` (about line 303) only creates the tray icon when `AppCloseBehavior == RunInBackgroundWithTrayIcon`. Keep that, but consider making tray mode the default for the fork.

## Phase 6: Rename the whole app to Mail-Cal

**Status (2026-10-23):** implemented, uncommitted. Remaining "Wino" mentions are only the attribution, historical upstream Sentry IDs in `iq/`, and the old names that `LegacyNameMigration` and the fallbacks below read. `MailFilter.IsMailCalCreated` keeps its database column through `[Column("IsWinoCreated")]`. The `MailCalShared` publisher cache folder declaration was left in place (harmless, legacy migration is off).

Goal: no "Wino" left in user-visible text, code, project and file names, build properties, scripts or docs. The only exceptions are the GPL-3.0 attribution (README credit to `bkaankose/Wino-Mail`, `LICENSE`, any upstream copyright headers, which the GPL requires to keep), the `upstream` git remote, and the git history.

Do it after Phases 1–5. The deletions shrink what has to be renamed, and the rename stays one mechanical commit with no feature work mixed in. Size today: 16 projects, about 1,450 C# files with `Wino` namespaces, and about 23,800 lines that contain "Wino".

**Trade-off to accept first:** after this phase, upstream fixes no longer merge cleanly. Every path and namespace differs, so pulling a fix from `bkaankose/Wino-Mail` becomes a manual port. Do any upstream merges you still want before this phase.

### Naming scheme

- Identifiers, namespaces, projects and files: `Wino` → `MailCal` (C# names can't contain `-`). For example `Wino.Core.Domain` → `MailCal.Core.Domain`, `Wino.Mail.WinUI` → `MailCal.Mail.WinUI`, `WinoLogger` → `MailCalLogger`, `IWinoRequestDelegator` → `IMailCalRequestDelegator`.
- User-visible text: "Wino Mail", "Wino Calendar" and "Wino" → "Mail-Cal" (or "Mail-Cal Calendar" where the app separates the two modes).
- Lowercase tokens (command-line switches, tags, file prefixes): `wino-` → `mailcal-`.
- Analyzer IDs: `WINOAOT001` → `MCAOT001` (update `AnalyzerReleases.Unshipped.md` and `AGENTS.md`).

### Code and build

1. **Projects and folders:** `git mv` every `src/Wino.*` and `controls/Wino.*` folder and its `.csproj`, so history follows the files. Update `ProjectReference` paths, `RootNamespace` / `AssemblyName`, and `InternalsVisibleTo`.
2. **Solution and workspace:** `WinoMail.slnx` → `MailCal.slnx`, plus any `.code-workspace`, `.vscode/launch.json` and `tasks.json`.
3. **Namespaces and types:** replace `Wino` in namespaces, `using` lines, type and member names, XAML `xmlns` / `using:` mappings, `x:Class`, and resource keys (`WinoDialogStyle`, `WinoApplicationBackgroundColor`, `WinoIllustration*Brush`, ...). Use a script, not hand edits, and review the diff for false hits inside provider-generated code (`src/Wino.Core/Outlook/Generated`).
4. **Build interface:** `WinoTargetPlatform` / `WinoTargetRuntimeIdentifier` → `MailCalTargetPlatform` / `MailCalTargetRuntimeIdentifier`, and `build/Wino.Windows.*` → `build/MailCal.Windows.*`. Update `Directory.Build.*` and `AGENTS.md`.
5. **Executables:** `Wino.Mail.WinUI.exe` and `Wino.NotificationHost.exe` get new names. Update the `com:ExeServer Executable=` entries in `Package.appxmanifest` and any script that looks for the exe (for example `Get-WinoOutputHash` in `start-wino.ps1`).
6. **Scripts:** `scripts/development/windows/start-wino.ps1` → `start-mailcal.ps1` (and its function names), `scripts/maintenance/windows/*`, `scripts/release/windows/*`, `scripts/README.md`, and the VS Code F5 configuration that calls them.
7. **Strings:** replace the brand in `src/Wino.Core.Domain/Translations/en_US/resources.json` (about 200 lines). The user asked for a full rename, so also replace the brand name in every other locale's `resources.json`. That's a mechanical brand-name swap only; leave the rest of those translations alone. Rename translation keys that contain `Wino` and their generated accessors.
8. **Other visible names:** the IMAP ID client name (`ImapClientPool`), the S/MIME `CertificateFriendlyName` ("Wino Mail Certificate"), window titles and `ReleaseIdentity.DisplayNames`, toast and notification host display names, and the About page (keep the credit line to upstream there).
9. **Docs:** `AGENTS.md`, `docs/**` (including `wino-design-guideline.md`, which gets a new file name), `controls/AGENTS.md`, `icons/README.md`, skills under `.claude/skills`, and the README.
10. **Artwork:** the logo, tile and tray icons in `src/Wino.Mail.WinUI/Assets` (including `StoreLogo.png`) and the What's New images show the Wino logo. They need new artwork. Ask the user for a logo, or make a simple placeholder. Check the icon font names in `icons/` too.

### Names stored on disk (migrate, don't just rename)

A plain rename would make existing data look missing. Each of these needs a one-time migration that runs on startup before any service opens the file, or it stays as it is:

| Name | Where | What to do |
|---|---|---|
| `Wino210.db` (+ `-wal`, `-shm`, `-journal`, `.migrating`) | `DatabaseService.cs:53-54`, `DatabaseMigrationCoordinator.cs:38` | **Done:** renamed to `MailCal210.db`; `LegacyNameMigration` moves the old files on startup before anything opens them. The upstream `Wino200` legacy path is dead code for this fork (it now reads `MailCal200.db`, which never exists) and can be removed later. |
| `Wino.DAV.{accountId}` DPAPI entropy | `DavCredentialStore.cs:86` | Changing it makes stored CalDAV/CardDAV passwords unreadable. Read with the old entropy as a fallback and re-protect with `MailCal.DAV.{accountId}` on success. Search for any other entropy or context strings that contain "Wino" and handle them the same way. |
| `WinoMailGmailTokenStore` folder | `AuthenticationTokenStorePaths.cs:12` | Move the folder on startup, or Gmail accounts must sign in again. |
| `WinoShared` publisher cache folder | `Package.appxmanifest:18`, `ApplicationConfiguration.cs:9`, `WinoApplication.cs:65` | Only used for the upstream legacy-data migration (`ReleaseIdentity.AllowsLegacyMigration`). Delete the folder declaration and the legacy migration path; don't rename. |
| Startup task `WinoStartupId`, background task `WinoSessionConnected` | `SessionStartActivation.cs:28-29`, manifest | Rename, and unregister the old background task. "Start with Windows" resets once; turn it back on in settings after updating. |
| `--wino-mail`, `--wino-session-start`, `wino-calendar`, `wino-todo`, ... | manifest `Arguments`, `AppModeActivationResolver.cs` | Rename to `mailcal-*`. Existing toasts and jump lists are short-lived, so no fallback is needed. |
| `WinoLocalDraftHeader` (mail header on drafts stored on the server) | `Constants.cs` | Write the new header; keep reading the old one, as the code already does for its predecessor. Otherwise existing drafts lose their local mapping. |
| `wino-backup-` export prefix and backup format markers | `WinoAccountSyncFileExportResult.cs:12` (moves in Phase 2) | New exports use `mailcal-backup-`; import still accepts old backup files. |
| Preference keys | `PreferencesService`, `PreferencesPropertyGenerator` (keys come from `nameof`) | Renaming a property that contains "Wino" changes its key and resets the setting. Map old keys to new ones once, or accept the reset for minor settings. |
| `wino:` protocol | manifest | Removed or renamed in Phase 0/2. Check that nothing still registers it. |

The package identity (`hairokuma.MailCal`) already changed in Phase 0, so the package data folder itself stays the same.

### Verification

- `rg -i wino` over the repo finds only the allowed exceptions above (attribution, license, the old names that migrations and fallbacks read on purpose). List each remaining hit in the handoff.
- Debug x64 build and the Release x64 gate pass; `format-xaml.ps1 -Check` passes.
- **Upgrade test:** install the last pre-rename build, sign in to an IMAP account, a Gmail account and a CalDAV account, and create a draft. Then install the renamed build over it. Accounts, mail, calendars, CalDAV passwords and Gmail tokens all keep working without signing in again, the draft still opens for editing, and `LocalState` has `MailCal.db` and no `Wino210.db`.
- Start menu entry, taskbar, title bars, toasts, tray tooltip, About page and Settings say "Mail-Cal"; the About page still credits Wino Mail as upstream.

## Phase 7: Signing that Smart App Control accepts (no paid certificate)

**Why it complains now:** Smart App Control (SAC) is on on the dev machine (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState = 1`). SAC runs a file only if Microsoft's cloud service already knows it as safe, or if it has a valid signature that chains to a CA in the Microsoft Trusted Root Program. The package is signed with the self-signed `CN=hairokuma` certificate. Putting that certificate in Trusted People or Trusted Root lets the MSIX install, but SAC ignores locally trusted roots. Every new build is unknown to the cloud service, so reputation never helps.

The Microsoft Store is out of scope: no Store listing, no Store signing.

**An unpackaged (standalone exe or zip) build doesn't avoid SAC.** SAC checks every exe and DLL the same way, whether it comes from an MSIX or not. An unsigned standalone build is just as unknown to the cloud service and gets blocked the same way. Dropping MSIX would also cost the package identity that toasts, the background task, the `wino:` protocol, publisher cache folders and startup registration depend on. So the app stays an MSIX, and the fix is the signature.

**Free option: SignPath Foundation** (free code signing for OSI-licensed open-source projects). The certificate is issued to "SignPath Foundation" by a public CA, so SAC accepts the signature, and the sideloaded MSIX no longer needs a manually trusted certificate.
- Requirements: a public GitHub repo under an OSI license (GPL-3.0 is fine), builds from GitHub Actions (SignPath verifies the build origin), a code-signing policy page in the repo, MFA for the maintainers, and a project that is already released and maintained. A brand-new personal fork may be refused; that is the main risk.
- The manifest `Publisher` must then match the certificate subject (the SignPath Foundation subject), not `CN=hairokuma`.
- Needs a GitHub Actions release workflow (Release x64 build, MSIX pack, SignPath signing step). That is new work; the repo currently packages locally.
- Distribute the signed `.msix` through GitHub Releases. Updates are manual: download the new release and install it over the old one. An optional `.appinstaller` file in the release can check for updates, but that contacts GitHub, so leave it out unless the user wants it.

**If SignPath refuses (paid, for reference only):** Azure Artifact Signing (formerly Trusted Signing, about $10/month) or Certum's open-source certificate (one-off fee). Otherwise the only choice left is to turn SAC off.

**What doesn't work:** an unpackaged build, self-signed or locally trusted certificates, Developer Mode, Dev Drive, and adding exclusions. SAC has no per-app allow list.

**Local development:** Debug builds that `winapp run` registers from loose files are unsigned and new on every build, so SAC can still block them (or DLLs they load) on a machine with SAC on. No free option changes that. Develop on a machine or VM with SAC off (for example the local Docker lab in `tools/local-lab`), and use the SignPath-signed build on the SAC-protected machine.

**Verification:**
- Install the SignPath-signed package on a machine with SAC on (`VerifiedAndReputablePolicyState = 1`), without importing any certificate.
- Launch from the Start menu and from the tray autostart; no SAC block notification appears.
- Event Viewer → `Applications and Services Logs/Microsoft/Windows/CodeIntegrity/Operational` shows no block events (3077/3033) for the package's files, including the background task host and WebView2 helper DLLs shipped in the package.

## Phase 8 (future, not now): Linux

No code for it now. To keep the port possible:
- Shared projects stay `net10.0` with no WinUI types.
- Keep the `WinoTargetPlatform` mechanism (`MailCalTargetPlatform` after Phase 6). Linux plugs in later through `build/MailCal.Linux.*` and a third value in `Directory.Build.props/targets`.
- The `WebView2PrintSettingsModel` / `IPrintService` naming leak in Core.Domain can be generalized when Linux work starts.

What the exploration showed for the later framework decision:
- **Uno:** reuses the most XAML (about 191 files and 5k `x:Bind` expressions), but its webview support on Linux is weak.
- **Avalonia or GTK4 via GirCore:** means rewriting the UI the way the macOS head did, but hosting WebKitGTK is straightforward.
- **MAUI:** has no Linux support.
- **Mail HTML rendering and editing** is the main blocker on any framework: it needs a WebKitGTK adapter for the `IHtmlMailReaderSession` / `IHtmlMailEditorSession` contracts in `controls/Wino.Editor.Core`.
- **Background:** upstream tried Uno and dropped it (`docs/appkit-platform-migration-plan.md`).

---

## Order and commits

Work in phases 0 → 6 on branch `standalone` (Phase 6, the rename, comes last so it is one mechanical commit; Phase 7, signing, is independent and can run at any time, but its manifest `Publisher` change is easiest after the rename), with one commit per phase, so the solution builds after every phase (`dotnet build` Debug x64). Phases 1–4 are mostly deletions, so the compiler finds the remaining references. Fix them as they come up instead of adding stubs, except for the attachment-detection stub in Phase 3. Commit only after the user agrees.

## Verification

1. **Build**, both on Windows in `C:\Users\fdoelling_ailio\source\Wino-Mail`:
   - `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64`
   - a Release x64 build, which runs the Native AOT and trimming warnings-as-errors gate. The repo treats this gate as release-blocking.
2. **No leftover endpoints:** this search must find nothing in `src/`, `controls/` or `build/`:
   `rg -i "winomail\.app|sentry|gravatar|google\.com/s2|favicon|thunderbird\.net|firetrust|dns\.google|appcenter|applicationinsights"`
   Check every `https://` literal that's left: each must be a provider endpoint (Graph, Google APIs, `login.microsoftonline.com`, Outlook) or user-configured.
3. **Runtime network check:**
   - Launch with `winapp run ... --arch x64 --detach`, with no accounts configured.
   - Wait about 60 s, then run `Get-NetTCPConnection -OwningProcess <pid>`.
   - Expect no outbound connections. A DNS/connection monitor such as Resource Monitor's Network tab is an alternative.
   - Then add an IMAP account manually and check that it syncs and that only the user's server is contacted.
4. **Regression smoke test:**
   - Add a 4th account; the limit should be gone.
   - Local backup export and import.
   - Mail list, reader and compose show no AI UI.
   - Settings has no Wino Account, Intelligence or Store entries.
5. **Tray:**
   - Enable "run in background with tray icon", then left-click the icon.
   - Today is selected, and the list matches the calendar page for today, including all-day events.
   - Clicking another date reloads the list.
   - Quick-add a timed event and an all-day event: each appears at once in the list and in the calendar page, and syncs to the provider.
   - The time pickers and calendar combo open without closing the popup.
   - Clicking an event opens it in the Calendar window and closes the popup.
   - "More options…" opens the full editor for the selected day.
   - Right-click still shows Show Mail-Cal / Show Calendar / Exit.
