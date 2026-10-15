# Wino Mail for Android: investigation

Update 14 October 2026: the decisions are settled in [Android design decisions](android-design-decisions.md), which supersede this study where they differ; the build guide is [Android development](harness/development-android.md).

Status: **investigation for review, nothing implemented**. Written 10 October 2026 against `main` at `aba4c07c`
(clean worktree). It follows the same route as the macOS work: first the shared-layer and nativeness
assessment, then a decision list, then the package order. Nothing below is a decision yet; the "Decisions
to settle" section is the list to review.

Related documents: [AppKit platform migration plan](appkit-platform-migration-plan.md) (project layout and
constraints the Mac port established), [macOS design decisions](macos-design-decisions.md) (Revision 2 parity
rules), [macOS shared-logic roadmap](macos-shared-logic-roadmap.md) (built from the local inventory of 59 places where the Mac head copied
logic instead of using shared code).

## 1. Summary

- **Shared backend reuse is the strong part.** The portable graph the Mac head already consumes
  (`Wino.Core.Domain`, `Wino.Services`, `Wino.Core`, `Wino.Authentication`, `Wino.Messaging`, the ViewModel
  projects, `controls/Wino.Editor.Core`, `controls/Wino.Mail.Controls.Core`) is plain `net10.0`, trimmable and
  Native AOT-clean because Windows ships it under Native AOT today. Every NuGet dependency in that graph has an
  Android asset or is pure managed code (table in section 3). The contracts the Mac port extracted
  (`ISecretProtector`, `IAccountCredentialPersistence`, `IGoogleTokenStore`, `IOutlookAuthenticationHost`,
  `IPlatformCapabilities`, `IApplicationRuntime`, `IDispatcher`, `INavigationService`, the editor sessions) are
  exactly the seams an Android head needs. Two shared pieces still assume a desktop: the Google loopback code
  receiver and the process-owned synchronization runtime (section 7).
- **Native UI is possible and is the Android analogue of the AppKit head:** .NET for Android with the classic
  Android View system (AndroidX + Material Components) written in C#, bound to the shared ViewModels with the
  same typed binding helpers the Mac uses. Jetpack Compose is not reachable from C# (Kotlin compiler plugin), so
  Views are the only native option. Google put the Views library in maintenance mode at I/O 2026 (section 5);
  it remains the stable, fully supported widget set and is what every non-Compose Android app ships with.
- **AOT: Native AOT for Android is still experimental in .NET 10.** Microsoft's support table lists Android as
  "Experimental, no built-in Java interop", and the dotnet/android tracking epic slipped from the .NET 10 to the
  .NET 11 and then the .NET 12 milestone (12 August 2026). The supported production mode is Mono full AOT with
  LLVM and trimming, which is still ahead-of-time native code, just with the Mono runtime instead of the
  CoreCLR-based Native AOT toolchain. Recommendation in D1: keep the codebase Native AOT-clean (already enforced
  by the Windows Release gate), ship Mono AOT, keep a `PublishAot=true` spike build as a gate so the switch
  happens the day it is viable. The Mac head ships `PublishAot=false` for the same reason.
- **The hard problems are not the controls.** They are (1) the background execution model (Android kills the
  process; Wino's runtime runs timers and IMAP IDLE in-process), (2) the OAuth redirect contracts (Google has
  deprecated loopback redirects for Android client types; MSAL needs the `msauth://package/signature-hash`
  redirect and signing-certificate registration), and (3) phone layouts, where "same layout as Windows" has to
  become a single-pane stack on compact widths while tablets keep the three-pane Windows layout.

## 2. What the Mac port established and Android inherits

| Mac element | Size today | Android equivalent | Notes |
| --- | --- | --- | --- |
| `src/Wino.Mail.MacOS` (head: AppDelegate, Composition, page registry, view controllers, dialog service) | ~52k lines incl. platform/bindings, ~250 files | `src/Wino.Mail.Android` (Application, single Activity, fragments or view controllers per Windows page name, Composition, manifest) | Keep the Windows view-name convention (`AboutPage` → `AboutPageFragment`) |
| `src/Wino.Platform.MacOS` (33 files: Keychain, protector, paths, auth host, token stores, launcher, clipboard, notifications, quarantine, S/MIME, presence) | | `src/Wino.Platform.Android` | Adapter inventory in section 6 |
| `controls/Wino.Presentation.AppKit` (`Bindings.cs`, `Layout.cs`, icons, zones, surfaces; 806 lines) | | `controls/Wino.Presentation.Android` | `PropertyBinding<TSource,TValue>`, `BindingScope` and `CommandBinding` reference only `Wino.Core.Domain` and MVVM Toolkit; move them to a neutral `Wino.Presentation.Core` so both heads share one binding engine (D12) |
| `controls/Wino.Mail.Controls.AppKit` (46 views, 8.7k lines) | | `controls/Wino.Mail.Controls.Android` | Same `Controls.Core` projections (`MailListCollection`, hover actions, context flyout models) |
| `controls/Wino.Editor.WebKit` (3 files, 519 lines: WKWebView session, reader/editor sessions) | | `controls/Wino.Editor.Android` | Android `WebView`; same `Editor.Core` sessions, assets and `SessionOperationQueue` |
| `src/Wino.Core.Apple.Bindings` (StoreKit 2 Swift bridge, Sparkle) | | Probably none | Play Billing and AndroidX ship as Microsoft-maintained NuGet bindings; no hand-written binding project unless a Java library lacks one |
| `WinoTargetPlatform=MacOS` property (collapses multi-TFM projects to `net10.0`) | in `Directory.Build.props`, `Wino.Mail.ViewModels`, `Wino.Mail.Controls.Core` | `WinoTargetPlatform=Android` | Same mechanism; `Wino.Core.ViewModels` and `Wino.Calendar.ViewModels` still declare both TFMs unconditionally and rely on `EnableWindowsTargeting`, as the Mac head does |

The alignment review found 59 places where the Mac head re-implemented shared behaviour (route table, flyout
builders, special-folder glyph map, theme catalogue, operation glyphs, date formatting, dialog rules, settings
menu provider). An Android head built the same way would become the third copy. The "Move" items in that
document are the cheapest preparation for Android and also fix Mac drift; see D11.

## 3. Shared layer portability inventory

Verified against the restored packages in `Directory.Packages.props` and public package listings.

| Dependency | Android status | Note |
| --- | --- | --- |
| MailKit / MimeKit 4.18 | Managed | TLS through `SslStream`; no change |
| sqlite-net-pcl 1.11 → SQLitePCLRaw.bundle_green 2.1.11 | Android asset (`net6.0-android31.0`, bundled `e_sqlite3`) | Native `.so`; verify 16 KB page alignment (section 8) |
| SkiaSharp 4.153 | Android asset | Native `.so`; verify 16 KB alignment |
| Microsoft.ML.OnnxRuntime 1.30/1.31 | `net9.0-android35.0` asset, XNNPACK provider | Optional. `Wino.Core.ML` already has a `WINO_ML_UNSUPPORTED` build (used for x86 and Intel Mac); Android can start without Magika and add it later |
| Microsoft.Identity.Client 4.90 | `net-android` asset, broker via Microsoft Authenticator built in | `Microsoft.Identity.Client.Broker` (Windows WAM) is referenced unconditionally by `Wino.Core`; harmless on Mac, must be checked under Android trimming or made Windows-only |
| Microsoft.Identity.Client.Extensions.Msal | Used by `Wino.Platform.Windows/MacOS` for the file cache | Not needed on Android; serialize the MSAL cache through `ISecretProtector` instead |
| Microsoft.Graph.Core 4 | Managed | |
| Sentry.Serilog 6.12 | `Sentry` supports .NET for Android directly and embeds the native Android SDK | Init in `Application.OnCreate`; `WinoLogger` already calls `SentrySdk.Init` |
| Serilog + File/Debug sinks | Managed | Log under `FilesDir/Logs` |
| HtmlAgilityPack, HtmlKit, Ical.Net 5, NodaTime, morelinq, Nito.AsyncEx, TimePeriodLibrary, EmailValidation, CommunityToolkit.Mvvm 8.4, Konscious Argon2 | Managed (`net8.0`/`netstandard` assets) | |
| Wino.Mail.Contracts, Wino.Mail.AI.* (own packages) | `net10.0` only | Fine for `net10.0-android` |
| System.Security.Cryptography.ProtectedData | Windows only | Already isolated in `Wino.Platform.Windows` |
| Wino.SourceGenerators | Analyzer | Unchanged |

Shared code concerns beyond packages:

| Place | Concern | Needed change |
| --- | --- | --- |
| `Wino.Authentication/WinoGmailCodeReceiver.cs` | Internal, loopback `HttpListener` on `127.0.0.1`. Google: "The loopback IP address redirect option is DEPRECATED for Android, Chrome app and iOS OAuth client types." | Extract an `IGoogleAuthorizationCodeReceiver` seam; `GmailAuthenticator` already takes a `redirectUri` factory, so the token exchange stays shared. Android implements it with a custom-scheme intent (section 6) |
| `Wino.Core/Services/ApplicationRuntime.cs` | `PeriodicTimer` auto-sync loops, IMAP IDLE pool and the calendar reminder poller assume a long-lived process | Add an explicit "synchronize once, bounded" entry point for WorkManager; the private `ExecuteAutoSynchronizationAsync` is the candidate (section 7) |
| `IDispatcher` | One method, `ExecuteOnUIThread` | Android main `Looper`/`Handler`; trivial |
| `IApplicationConfiguration` paths | `PublisherSharedFolderPath`, temp, data | `Context.FilesDir`, `CacheDir`, `NoBackupFilesDir`; `AllowLegacyDataMigration=false` like Mac |
| Preferences / `IConfigurationService` | Mac uses a file-backed implementation | Reuse the same approach; `SharedPreferences` not required |
| WinoIcons fonts, theme wallpapers, What's New assets | Bundled as Mac `BundleResource` | Android assets (`AndroidAsset`), `Typeface.CreateFromAsset` |
| Reflection and JSON | Shared layer already uses source-generated JSON and no reflection serialization | Nothing; the Windows Release gate keeps it that way |
| `IUserPresenceStateProvider`, `INotificationBuilder` | Windows-shaped wording (taskbar, jump list, WebView2 runtime notification) | Mac already mapped these to Dock badge and Dock menu; Android maps to app shortcuts and notification badges |

## 4. Runtime and AOT options on Android

| Mode | Status in .NET 10 | What it means for Wino |
| --- | --- | --- |
| Mono JIT (Debug) | Supported | Development loop, fast deploy |
| Mono AOT: `RunAOTCompilation=true`, `AndroidEnableProfiledAot` or full AOT, `EnableLLVM=true`, `TrimMode=full`, `AndroidEnableMarshalMethods=true` | Supported, the production path for .NET Android and MAUI apps | Ahead-of-time native code with the Mono runtime; no JIT at runtime for AOT-compiled methods; startup and size tuned by profile. Satisfies "AOT enabled" today |
| Native AOT: `PublishAot=true` | Experimental. Microsoft's table: "Android x64, Arm64, Arm: Experimental, no built-in Java interop". dotnet/android epic #9784 milestone moved to .NET 12 on 12 Aug 2026; open items include "address all trimming and AOT warnings", `libc++` linking and FastDeploy. Requires NDK r27+, emits warning XA1040 | Not a shipping option today. Reported wins are large (startup roughly 3 to 4 times faster than Mono AOT in community measurements), so it is worth a standing spike build |

Recommendation (D1): Mono full AOT + LLVM + full trimming for Release; keep `IsAotCompatible` and the analyzer
discipline from `AGENTS.md`; add a non-blocking Native AOT publish to the release gate that records warnings
and size, so the decision to switch is data-driven. Java interop is the risk under Native AOT: the generated
type map replaces runtime registration, and bindings libraries (AndroidX, Material, Billing) are compile-time
generated, so in principle compatible, but unproven for this dependency set.

Platform facts that bound the project:

- `net10.0-android` targets API 36 (Android 16); the minimum supported `SupportedOSPlatformVersion` is 21.
  Google Play requires new apps and updates to target API 36 since 31 August 2026 (extension to 1 November 2026).
- 16 KB memory pages: Play requires apps targeting API 35+ to support 16 KB pages on 64-bit devices; after
  1 February 2027 updates without it are blocked. Only native `.so` files matter: the .NET runtime libraries,
  `e_sqlite3`, `libSkiaSharp`, `onnxruntime`, and Sentry's native SDK must be checked with the NDK alignment
  script on a 16 KB emulator image during the foundation spike.
- 64-bit only (`arm64-v8a`, plus `x86_64` for the emulator). Dropping `armeabi-v7a` is normal for 2026 apps and
  halves native payload.

## 5. Native UI options

### 5.1 Options considered

| Option | Verdict |
| --- | --- |
| Android Views (`android.widget`, AndroidX, Material Components) in C# through .NET for Android bindings | **The native path.** Same relationship to Android as AppKit has to macOS: platform widgets, platform text, platform scrolling, accessibility (TalkBack), IME, RTL, dynamic colour. Authored in C# code with small layout helpers, as on the Mac, or in XML layouts under `Resources/layout`. Recommend code-first like the Mac head, XML only where Android tooling expects it (notification layouts, app widgets, splash) |
| Jetpack Compose | Not reachable from C#; Compose requires the Kotlin compiler plugin. Out |
| MAUI, Uno | Excluded by the user |
| Hybrid WebView UI | Not native; out |

Material Components for Android (Views) status: the project README says the Views-based library "is now in
maintenance mode" after Google announced at I/O 2026 that Material Android is "all-in" on Compose. For Wino this
means bug fixes but no new components. The library is mature (Material 3 theming, every component Wino needs)
and the .NET binding `Xamarin.Google.Android.Material` tracks it (1.12.x as the latest found; verify the
version on NuGet at project start). Risk is low in the port horizon; long term Wino draws many of its own
surfaces anyway (section 5.3), so dependence on Material is limited to chrome and inputs.

### 5.2 Layout: same as Windows, adapted by window size class

Android's window size classes decide the Windows-parity layout:

| Width class | Devices | Layout |
| --- | --- | --- |
| Expanded (≥ 840 dp) | Tablets, foldables open, desktop mode, ChromeOS | Windows layout: navigation pane (accounts, folders, mode switcher card at the foot), mail list zone, reader zone; title bar actions in a top app bar; theme wallpaper behind everything per Revision 2 |
| Medium (600 to 839 dp) | Small tablets, foldables | List and reader side by side (`SlidingPaneLayout`), folders in a modal navigation drawer |
| Compact (< 600 dp) | Phones | Stacked: folders in a drawer, list full screen, reader full screen (fragment push), compose full screen, Settings as its own fragment stack with the Windows groups and order |

Mode switcher on compact widths is the one place Android convention and Windows layout disagree: a drawer
footer is invisible most of the time on a phone. Options in D3. Settings keeps the Windows groups, page names
and card order (the "where was that setting" rule from the Mac decisions).

### 5.3 Control mapping

| Windows | macOS (decided) | Android |
| --- | --- | --- |
| `ShellWindow`, custom title bar | `NSWindow`, unified `NSToolbar` | Single `Activity`, `MaterialToolbar` / `AppBarLayout`, edge-to-edge with `WindowInsets` |
| NavigationView pane, folders | `NSOutlineView` source list | `NavigationView` drawer (compact) or permanent pane (expanded) hosting a `RecyclerView` with Wino shell rows |
| `AppModeFooterSwitcherControl` | Segmented card in sidebar footer | Drawer footer card (expanded); `BottomNavigationView` or `NavigationRailView` (compact), see D3 |
| `WinoMailListView` | View-based `NSTableView` over `MailListCollection` | `RecyclerView` + `ListAdapter`/`DiffUtil` over `MailListCollection`; stable ids; `ItemTouchHelper` for swipe actions; hover actions become long-press and context menu |
| Reader `WinoMailRenderer` (WebView2) | `WKWebView` | `WebView` (system Chromium, updatable) through `Editor.Core`; `WebViewAssetLoader` for shell assets, `shouldInterceptRequest` for remote-content policy (Windows-style interceptor is available here), `shouldOverrideUrlLoading` → `NavigationRequested`, `WebMessageListener` or `addJavascriptInterface` for the bridge |
| Composer editor + tabbed command bar | `WKWebView` + in-content toolbar | `WebView` + a horizontally scrolling format bar above the IME (`adjustResize`/`WindowInsets.ime`), `OnReceiveContentListener` for pasted images |
| Recipient `TokenizingTextBox` | `NSTokenField` | `ChipGroup` + `AutoCompleteTextView` (no native token field); validation stays shared |
| Search box with suggestions | `NSSearchField` + scope bar | Material 3 `SearchBar`/`SearchView` with chips for Local/Online and filters |
| Settings cards / expanders | `WinoSettingsCard`/`WinoSettingsExpander` NSViews | Wino-drawn card views (same API) inside a `RecyclerView`; `MaterialSwitch`, exposed dropdown `TextInputLayout`, `Slider` inside |
| ContentDialog forms | `NSWindow` sheets, `NSAlert` | `MaterialAlertDialogBuilder` prompts, `DialogFragment` forms, `BottomSheetDialogFragment` for pickers (Move mail, filters) |
| Flyouts / context menus | `NSMenu`, `NSPopover` | `PopupMenu`, `ListPopupWindow`, bottom sheets; shared `ContextFlyoutMenuEntry` models |
| Date/time pickers, range slider | `NSDatePicker`, custom range | `MaterialDatePicker`, `MaterialTimePicker`, `RangeSlider` (native dual thumb exists) |
| Calendar surfaces | Custom `NSView` + `CalendarLayoutCalculator` | Custom `View`; move `CalendarLayoutCalculator` to `Controls.Core` first (alignment item 44) |
| InfoBar, badges, avatars, chips, intelligence header | Wino-drawn | Wino-drawn `View`s, same C# surface |
| Daily briefing panel | Right overlay | Side sheet (expanded) or bottom sheet / full screen (compact) |
| Pop-out windows, tray, companion, global hotkeys | Mac equivalents | None. `AdditionalWindows`, `Tray`, `GlobalHotkeys`, `StartupIntegration` = false. Android split-screen is the OS's, not Wino's |
| Toast notifications, taskbar badge, jump list | `UNUserNotificationCenter`, Dock badge/menu | `NotificationManagerCompat` with per-account channels and action buttons (Archive, Delete, Mark read via `BroadcastReceiver`); launcher badge from notification count; folder shortcuts via `ShortcutManagerCompat` |

Icons: WinoIcons fonts everywhere, as Revision 2 requires; Material icons are not used for Wino concepts.

## 6. Platform adapter inventory

Each row is a registration in the Mac `Composition.cs` and its Android counterpart.

| Contract | Mac | Android implementation | Risk |
| --- | --- | --- | --- |
| Paths, `IApplicationConfiguration` | `MacOSPaths` | `FilesDir` (data), `CacheDir` (temp), `NoBackupFilesDir` for the database and secrets. Opt the app out of Auto Backup for db/secrets (`dataExtractionRules`); Wino has its own backup/restore | Low |
| `ISecretProtector` | Keychain-held AES-256-GCM key, `WMKP` envelope | Android Keystore (`AndroidKeyStore`) AES-256-GCM key, non-exportable, `setRandomizedEncryptionRequired` default (Keystore supplies the nonce), same envelope format. Move the envelope code out of `MacOSSecretProtector` into a shared `AesGcmEnvelopeProtector` taking a key provider. Keystore keys die with uninstall or factory reset: acceptable, same as Keychain. No plaintext fallback; unavailable protection → `PlatformNotSupportedException` → re-authentication | Medium: OEM Keystore bugs, StrongBox failures; need the "corrupt vs unavailable vs missing" distinction the contract already demands |
| `IAccountCredentialPersistence`, `IDavCredentialStore`, `IGoogleTokenStore` | Keychain revisions / encrypted files | Protected files under `NoBackupFilesDir/credentials`, atomic replace, revision retention as on Mac | Low |
| `IOutlookAuthenticationHost` | `MacOSOutlookAuthenticationHost`, `WithDefaultRedirectUri` | MSAL Android: redirect `msauth://<package>/<base64 signature hash>`, `BrowserTabActivity` intent filter in the manifest, `WithParentActivityOrWindow(activity)`, `AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs` from `OnActivityResult`; broker through Microsoft Authenticator when installed, system browser otherwise; cache serialized through `ISecretProtector`. Entra app registration `b19c2035-…` needs an Android platform entry with the package name and both signing hashes (upload key and Play App Signing key) | Medium: registration and signing-hash hygiene; verify MSAL under full trimming |
| Gmail code receiver (new seam) | Loopback | Google Cloud OAuth client of type **Android** (package + SHA-1), redirect `com.googleusercontent.apps.<client-id>:/oauth2redirect`, no client secret, PKCE (already implemented). Google disabled custom URI schemes for new Android clients by default in October 2023 but the client owner can enable them under Advanced Settings; loopback is deprecated for Android client types. Open the authorization URL in Chrome Custom Tabs (`androidx.browser`), never a WebView (`disallowed_useragent`). An exported activity with the scheme intent filter receives the code | Medium: console setting, verification of the Wino project's OAuth consent configuration; alternative is Google Identity Services, which adds Play Services dependence and does not give the refresh-token flow Wino uses |
| `IExternalBrowserAuthenticationPresenter` | Sheet with cancel and copy link | Same UI as a dialog; redirect brings the app back via the intent | Low |
| `IExternalLauncher` | `NSWorkspace` | `Intent.ActionView`, Custom Tabs for http(s) | Low |
| `IClipboardService` | `NSPasteboard` | `ClipboardManager` | Low |
| `IShortcutPlatformService` | Cmd modifier | Ctrl (external keyboards on tablets); `ModifierKeys` already abstracted | Low |
| `IAttachmentPlatformService`, `IFileService` | Quarantine, Finder | `FileProvider` content URIs + `ACTION_VIEW`; save via `ACTION_CREATE_DOCUMENT` or MediaStore Downloads; logs export via SAF | Low |
| `IReaderRuntimeService` | always available | `WebViewCompat.GetCurrentWebViewPackage` null check (WebView disabled or missing on some devices) | Low |
| `INotificationBuilder` | `MacNotificationBuilder` | Channels per account, `POST_NOTIFICATIONS` runtime permission (API 33+), actions via `BroadcastReceiver`, grouping by account, badge count; calendar badge → shortcut/notification; "WebView2 missing" → "WebView missing" | Medium: the shared policy (privacy levels, quiet hours) already exists; the work is plumbing |
| `IUserPresenceStateProvider` | Presentation detection | `NotificationManager.CurrentInterruptionFilter` for DND; no presentation state | Low |
| `ICalendarReminderServer` | In-process poller | `AlarmManager` exact alarms. `SCHEDULE_EXACT_ALARM` is a user-granted permission on API 31+; `USE_EXACT_ALARM` is reserved for alarm and calendar apps under Play policy. Wino has a Calendar mode; whether it qualifies is a store-review question (D8) | Medium |
| `INewThemeService` | `MacWinoThemeService` with a copied catalogue | Android theme service over the shared catalogue after alignment item 50 moves it; dynamic colour (Material You) as an additional "System" theme; wallpaper backdrop per Revision 2 | Low |
| `IMicrosoftStoreService`, `IAppStoreClient` | Store unavailable; StoreKit 2 on App Store builds | `MicrosoftStore=false`, `AppleAppStore=false`. Play Billing through `Xamarin.Android.Google.BillingClient` 9.0 if purchases are offered on Android (D7). `IAppStoreClient` is StoreKit-shaped (ulong transaction ids, Finish, Sync); a Play implementation needs a small generalization or a sibling contract | Medium: policy, not code |
| `IMailDialogService` (+7 Mac partials) | `AppKitDialogService` | Android dialog service: alerts, form `DialogFragment`s, bottom sheets; same consequence-led button labels; the hard-delete confirm and don't-ask-again rules should move to a shared dialog base first (alignment item 54) | High volume, low risk |
| `INavigationService`, page registry | `AppKitNavigationService`, `MacPageRegistry` | Single `Activity` + `FragmentManager`; mode-scoped back stacks; predictive back (`OnBackPressedCallback`, Android 13+ gesture); route table shared after alignment items 4 and 13 to 19 | Medium: the Mac history list shows what goes wrong when the host invents its own rules |
| `IMailPrintPresenter`, PDF | Mac native printing | `PrintManager` with `WebView.CreatePrintDocumentAdapter`; PDF via the same adapter. Cheap on Android; can be in scope early | Low |
| `ISmimeCertificateService` | Keychain certificates | `KeyChain.ChoosePrivateKeyAlias` + `KeyChain.GetPrivateKey`; feasible but defer (`Smime=false` in v1) | Deferred |
| Companion, tray, global hotkey, startup | Menu bar extra, Carbon hotkey, login item | None; capabilities false. A home-screen widget could replace the Companion later (out of scope) | None |
| `IAppMetadataService` | Bundle strings | `PackageManager` version name/code, Sentry release/dist | Low |

## 7. Background execution and synchronization

This is the design problem that has no desktop precedent.

Current model: `ApplicationRuntime.StartAsync` owns `PeriodicTimer` auto-sync loops for mail and calendar, the
IMAP client pool keeps an IDLE connection per account (`ImapSynchronizer.StartIdleClientAsync`), and the
calendar reminder server polls. Windows and Mac keep the process alive (tray, menu bar extra, "keep syncing
after the last window closes"). Android does not: the process is frozen or killed in the background, Doze
batches network, and from Android 15 (apps targeting API 35+) a `dataSync` foreground service gets 6 hours per
24 hours and then `onTimeout` with a forced stop, reset only when the user opens the app. A permanent IMAP IDLE
service is therefore not an option for an app that targets API 36, which Play requires.

Proposed model:

1. **Foreground**: the Activity is visible → the runtime starts exactly as on desktop (IDLE, loops, reminders).
   Stop it in `onStop` after a short grace period so the process can be frozen cleanly.
2. **Background sync**: WorkManager `PeriodicWorkRequest` with a network constraint, interval = the user's sync
   interval setting rounded up to the 15-minute floor, running a new bounded `IApplicationRuntime` entry point
   ("synchronize all accounts once, with cancellation and a deadline") in the app process. The DI container
   must be constructible without UI; the Mac `Composition.Create` already separates the graph from windows, and
   runtime initialization is checkpointed, so an `Application`-owned container that both the Activity and the
   worker attach to is the natural shape.
3. **Pending changes**: every queued `WinoRequestProcessor` change enqueues a one-time expedited work item, and
   `onStop` flushes, so sends and flag changes survive process death.
4. **Notifications** come from the background sync result through the existing `INotificationBuilder` gate; no
   separate notification path.
5. **Reminders**: `AlarmManager` instead of the poller (section 6).
6. **Draft safety**: save in `onStop`; the Mac rule "no quit with unsaved drafts" becomes "no background without
   a saved draft".
7. **User expectation management**: a settings explainer about battery optimization (OEM task killers) and the
   15-minute floor. Push-like latency for Gmail and Outlook would need a Wino relay server subscribing to
   provider webhooks and forwarding through FCM; it is listed here as a later option, not part of the port.

What this costs in shared code: one new runtime entry point, an `IApplicationRuntime` host-mode flag or
two start modes, and tests for "bounded sync completes or cancels within deadline". No synchronizer changes.

## 8. Project layout, build and distribution

| Project | Target | Notes |
| --- | --- | --- |
| `src/Wino.Mail.Android` | `net10.0-android`, `SupportedOSPlatformVersion` per D4, `WinoTargetPlatform=Android` | `MainApplication` (container, Sentry, WorkManager init), `MainActivity` (single activity, edge-to-edge), fragments per Windows page name, `AndroidComposition`, `AndroidManifest.xml` (intent filters for `msauth` and the Google scheme, `POST_NOTIFICATIONS`, `SCHEDULE_EXACT_ALARM`, WorkManager) |
| `src/Wino.Platform.Android` | `net10.0-android` | Section 6 adapters; references Domain, Authentication, Services only |
| `controls/Wino.Presentation.Core` (new, neutral) | `net10.0` | Binding scope, property and command bindings extracted from `Presentation.AppKit` |
| `controls/Wino.Presentation.Android` | `net10.0-android` | Layout helpers, WinoIcons typeface, zones, theme surfaces |
| `controls/Wino.Mail.Controls.Android` | `net10.0-android` | Reusable views over `Controls.Core` |
| `controls/Wino.Editor.Android` | `net10.0-android` | WebView sessions over `Editor.Core` |

Build and tooling facts:

- Everything builds on the Windows development machine: Android workload (`dotnet workload install android`),
  JDK 17, Android SDK platform 36, build tools, emulator images (including a 16 KB page-size image), NDK r27+
  for the Native AOT spike. No Mac in the loop, unlike the macOS port.
- Commands follow the repository style: `dotnet build src/Wino.Mail.Android -f net10.0-android -t:Run` for
  deploy-and-run on the emulator; `dotnet publish -c Release -r android-arm64` for the AAB; `adb logcat` for
  logs. The VS Code F5 helper would get an Android launch configuration.
- Lab: the Docker lab is reachable from the emulator at `10.0.2.2` (host loopback); physical devices need the
  host's LAN address and the lab ports bound beyond loopback. The lab README is Windows-centric and would gain
  an Android paragraph.
- Signing and store: Play Console developer account, upload key, Play App Signing (two certificate hashes to
  register with Entra and Google OAuth), AAB upload, internal testing track for the lab phase. Sideload APK
  releases on GitHub stay possible, like the DMG channel; no Sparkle equivalent is needed because Play updates
  the app (optional Play In-App Updates API later).
- Play policy items to plan for: Data safety form; account deletion in-app and via web for apps with account
  creation (Wino Account); exact-alarm justification; target API deadline each August; 16 KB pages.
- Package id (`applicationId`) cannot change after the first Play upload; Mac uses `com.winomail.macos`.

## 9. Decisions to settle (with recommendations)

| ID | Decision | Options | Recommendation |
| --- | --- | --- | --- |
| D1 | Runtime mode | Mono AOT now; Native AOT now; interpreter | **Mono full AOT + LLVM + full trimming** for Release; standing `PublishAot=true` spike in the gate; switch when the dotnet/android epic closes |
| D2 | Form factors in scope for v1 | Phone only; tablet only; adaptive | **Adaptive from day one** (compact and expanded layouts, medium = two-pane). Building compact-only first and retrofitting panes is how layout parity gets lost |
| D3 | Mode switcher on phones | Drawer footer card (Windows position); `BottomNavigationView`; `NavigationRailView` | **Bottom navigation on compact** (Mail, Calendar, Contacts, To Do; Settings through the drawer and account avatar), **drawer footer card on expanded** exactly as Windows |
| D4 | Minimum SDK | 21 (.NET floor), 24, 26, 28 | **26 (Android 8)**: notification channels, autofill, adaptive icons, covers effectively all active devices |
| D5 | Google sign-in mechanism | Android OAuth client + custom scheme (enable in Cloud Console Advanced Settings); Google Identity Services | **Custom scheme + Custom Tabs**. Action for the project owner: confirm the toggle is available for the Wino Cloud project and register the Android client (package + both SHA-1s) |
| D6 | Background sync | WorkManager only; WorkManager + optional user-enabled foreground service for IDLE; relay server | **WorkManager only in v1**, foreground sync while visible, honest 15-minute floor in Settings. Relay server is a product decision outside the port |
| D7 | Purchases on Android | Play Billing in v1; entitlement sign-in only; none | **Entitlement sign-in only in v1** (Wino Account purchased elsewhere, no purchase links in-app to stay within Play policy), Play Billing in a later release over a generalized store contract |
| D8 | Deferred features | | Defer S/MIME, companion/tray/hotkeys (not applicable), additional windows (not applicable), device-contacts integration, Magika/ONNX. Keep printing/PDF (cheap on Android). Exact alarms: request `SCHEDULE_EXACT_ALARM`, fall back to inexact when denied |
| D9 | Data location and Auto Backup | | Database and secrets under `NoBackupFilesDir`, Auto Backup excluded; Wino backup/restore via SAF |
| D10 | Identity | | `com.winomail.android` (parallel to `com.winomail.macos`); Play listing assets reuse Windows/Mac artwork |
| D11 | Shared-layer preparation | Start Android directly; first do the "Move" items from the Mac alignment review | **Do the Move items first** (route table and re-entry rules, flyout builders, glyph maps, operation presentation, theme catalogue, date/colour formatting, dialog rules, settings menu provider). They are needed by both heads and prevent a third copy |
| D12 | Presentation helpers | Copy `Bindings.cs` into an Android library; extract neutral core | **Extract `Wino.Presentation.Core`** and make `Presentation.AppKit` consume it |
| D13 | Authoring style | XML layouts; C# code-first | **Code-first C#** with layout helpers, mirroring the Mac head; XML only where Android requires it |

## 10. Challenges and risks, ranked

1. **Background reliability and notification latency.** Process death, Doze, OEM killers and the 15-minute floor
   change what "sync" means versus desktop. Mitigation: the two-mode runtime in section 7, expedited work for
   outgoing changes, clear settings copy.
2. **Native AOT not production-ready; Mono AOT trade-offs.** Larger APK, slower cold start than Native AOT,
   trimming of Java bindings. Mitigation: profiled AOT, startup tracing, the spike gate.
3. **OAuth contracts.** Two external registrations (Entra Android platform, Google Android client), two signing
   hashes each, a Google console toggle, and a new receiver seam in shared code. Mitigation: do it in the first
   package with the production package id so no re-registration follows.
4. **UI volume.** The Mac head reached parity with roughly 52k lines; Android needs compact and expanded
   variants of the shell, list, reader and composer, so expect at least the same volume. The 29 settings pages,
   24 dialogs and the page registry are mechanical once the shared "Move" items exist.
5. **WebView on touch.** IME resizing, selection handles, format bar placement, pasted images, system WebView
   version spread. Mitigation: `Editor.Core` sessions are proven; keep the Android session as thin as the Mac one.
6. **Keystore behaviour across OEMs.** Mitigation: the contract already distinguishes corrupt, unavailable and
   missing; never erase an unreadable store; re-authentication path.
7. **Material Views in maintenance mode.** Low impact in the port horizon; Wino draws its own cards, rows and
   chips.
8. **Verification.** Repository rules exclude agent-driven UI automation; manual checks on emulator and device
   against the lab, unit tests for pure adapters, and device-only tests for Keystore and WorkManager glue.

## 11. Proposed package order

| Package | Deliverable | Depends on |
| --- | --- | --- |
| A0 | Shared-layer "Move" items from the Mac alignment review; `Wino.Presentation.Core` extraction; Google code-receiver seam; bounded runtime entry point | None |
| A1 | Android foundation: project skeleton, portable graph compiling for `net10.0-android`, headless composition in `Application`, paths, Keystore protector, credential stores, dispatcher, logging/Sentry, Mono AOT Release publish, Native AOT spike record, 16 KB check | A0 |
| A2 | Authentication and onboarding: MSAL Android host, Google custom-scheme receiver, Welcome, provider selection, IMAP setup pages | A1 |
| A3 | Shell and mail: drawer/pane, mode switching, mail list (`RecyclerView`), reader (WebView session), search, compact and expanded layouts | A1, A2 |
| A4 | Compose: editor session, recipients, attachments, drafts, send | A3 |
| A5 | Settings: all groups and pages, dialogs, theme service | A3 |
| A6 | Background: WorkManager sync, pending-change flush, notifications, exact-alarm reminders | A1 |
| A7 | Calendar, Contacts, To Do, daily briefing, What's New | A3 |
| A8 | Distribution: Play Console, signing, internal testing, policy forms; sideload APK | A1 |

A1 through A2 form the prototype gate, like the Mac OAuth/sidebar prototype (D7 in the Mac plan). A6 can run in
parallel with A3 to A5 once A1 exists.

## 12. Verification approach for the port

- Builds: Debug `net10.0-android` deploy to emulator; Release Mono AOT publish; Native AOT spike publish (record
  warnings, size, startup; non-blocking).
- Tests: existing `Wino.Core.Tests` and ViewModel tests on the portable graph; new pure tests for the bounded
  runtime entry point and the Google receiver seam; device tests only where the adapter is Android-only.
- Manual lab checks on the emulator (`10.0.2.2`) with the three fixture accounts, on a 16 KB image, on a phone
  and a tablet profile, light and dark, TalkBack, external keyboard.
- Windows Debug and Release gates unchanged for every shared-layer change (A0 touches shared code).

## 13. Open questions for the owner

1. Does the Wino Google Cloud project allow enabling custom URI schemes for a new Android OAuth client? (D5)
2. Is Play distribution the goal, or sideload first? Play drives the API, 16 KB, billing and account-deletion
   requirements. (D7, section 8)
3. Minimum SDK and whether to ship `x86_64` only for the emulator or also to Play (ChromeOS). (D4)
4. Confirm the compact-width mode switcher choice. (D3)
5. Whether A0 (shared-layer moves) is done before Android starts or interleaved. (D11)

## Sources

- [Native AOT deployment overview, platform table](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [dotnet/android NativeAOT TODO epic #9784](https://github.com/dotnet/android/issues/9784)
- [dotnet/android NativeAOT publish failure on .NET 10 RC2 (NDK)](https://github.com/dotnet/android/issues/10587)
- [Uno Platform Native AOT notes (NDK r27, XA1040)](https://platform.uno/docs/articles/features/native-aot.html)
- [XA4216 minimum Android API level](https://learn.microsoft.com/en-us/dotnet/android/messages/xa4216)
- [Google Play target API level requirements](https://developer.android.com/google/play/requirements/target-sdk)
- [Android 16 KB page sizes](https://developer.android.com/guide/practices/page-sizes)
- [Android 15 behaviour changes, foreground service timeouts](https://developer.android.com/about/versions/15/behavior-changes-15)
- [Foreground service timeout](https://developer.android.com/develop/background-work/services/fg-service-timeout)
- [Google OAuth 2.0 for mobile and desktop apps](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Google OAuth custom URI scheme restrictions (October 2023)](https://developers.googleblog.com/improving-user-safety-in-oauth-flows-through-new-oauth-custom-uri-scheme-restrictions/)
- [Microsoft identity platform: Android redirect URI and BrowserTabActivity](https://learn.microsoft.com/en-us/entra/identity-platform/tutorial-mobile-app-android-prepare-app)
- [MSAL.NET Android broker migration](https://learn.microsoft.com/entra/msal/dotnet/how-to/migrate-android-broker)
- [Microsoft.ML.OnnxRuntime on NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime)
- [SQLitePCLRaw.bundle_green on NuGet](https://www.nuget.org/packages/SQLitePCLRaw.bundle_green)
- [Sentry for .NET Android](https://docs.sentry.io/platforms/dotnet/guides/android)
- [Xamarin.Android.Google.BillingClient on NuGet](https://www.nuget.org/packages/Xamarin.Android.Google.BillingClient)
- [Material Components for Android README (maintenance mode)](https://github.com/material-components/material-components-android)
