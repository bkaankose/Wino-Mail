# Wino Mail for iOS and iPadOS: platform plan

## Context

Wino Mail ships a WinUI app and a native AppKit app over one shared .NET backend. The iOS/iPadOS designs are settled (canvas <https://claude.ai/artifact/9iAW48NVfnnMNZTh39YhQL>, 13 Oct 2026): C# UIKit, native controls first, iPhone tab bar with four modes, iPad mirroring the Mac shell, Settings never a mode, Wino zones and WinoIcons everywhere. The Mac port proved the model: thin platform adapters, explicit C# bindings to shared ViewModels, no Uno/MAUI/Catalyst.

Today the Apple-specific code is all macOS-shaped: `Wino.Platform.MacOS` mixes Foundation-only services (Keychain, paths, config, credential stores, StoreKit client, notification categories, WebKit reader runtime) with AppKit-only ones (pasteboard, Carbon hotkeys, login items, status item). `Wino.Core.MacOS.Bindings` ships a macOS-only StoreKit2 xcframework plus Sparkle. The head duplicates business rules that the roadmap (`docs/macos-shared-logic-roadmap.md`) already plans to move into shared code. An iOS head built on this as-is would become a third copy.

Goal: restructure the Apple layer so three Apple apps share it, then add one universal iOS app with two UI projects (phone, pad), following the Mac implementation principles.

## Decisions (settled with the user, 13 Oct 2026)

| Topic | Decision |
| --- | --- |
| Packaging | One universal iOS app bundle (`src/Wino.Mail.iOS`), two UI libraries (`Wino.Mail.UIKit.Phone`, `Wino.Mail.UIKit.Pad`) selected at scene launch by `UIDevice.CurrentDevice.UserInterfaceIdiom`. A `net10.0-ios` project cannot be iPad-only at the SDK level, so the split is in code, not bundles. |
| Shared Apple layer | `src/Wino.Platform.Apple` (multi-target `net10.0-macos;net10.0-ios`) and `src/Wino.Core.Apple.Bindings` (same TFMs). `Wino.Platform.MacOS` keeps AppKit-only services; new `src/Wino.Platform.iOS` holds UIKit-only ones. |
| Minimum OS | iOS/iPadOS 17.0 (`SupportedOSPlatformVersion`). macOS stays 14.0. |
| First-pass scope | All four modes (Mail, Calendar, Contacts, To Do) plus onboarding and Settings at full fidelity on both phone and pad. |
| Runtime | Mono AOT (device) and interpreter (simulator Debug), `PublishAot=false`, mirroring the Mac head's exemption. Shared libraries keep the Native AOT gate (they are already AOT-clean, so a later NativeAOT-for-iOS evaluation is cheap). |
| Bundle identity | `com.winomail.ios`, team 4VB7YWRAQ9, as its own App Store record. Purchases already link cross-platform through the Wino Account API, so universal purchase (which would force the Mac bundle ID) is not needed. |
| Design rules | `docs/ios-design-decisions.md` (new) records the canvas decisions; the Mac Revision 2 rules (wallpaper backdrop, zones, WinoIcons, Windows row parity, mode switcher card on iPad) apply unless the iOS doc overrides them. |
| Not in this plan | APNs push relay integration (separate workstream per the 10 Oct 2026 relay decision; the head only registers for remote notifications), Share Extension, widgets, Mac Catalyst, Companion/menu-bar/hotkeys (not applicable), S/MIME on iOS (deferred: no system trust-anchor API). |

## Target project map

```
src/
  Wino.Platform.Apple/           net10.0-macos;net10.0-ios   Foundation/Security/StoreKit/UserNotifications/WebKit services
  Wino.Core.Apple.Bindings/      net10.0-macos;net10.0-ios   StoreKit2 (both), Sparkle (macos TFM only)
  Wino.Platform.MacOS/           net10.0-macos               AppKit-only: pasteboard, hotkeys, login items, presence, quarantine, trust anchors, Shift check
  Wino.Platform.iOS/             net10.0-ios                 UIKit-only: UIPasteboard, UIApplication.OpenUrl, QuickLook attachments, MSAL iOS host, badge/sounds
  Wino.Mail.MacOS/               (unchanged head, references Platform.Apple + Platform.MacOS)
  Wino.Mail.UIKit/               net10.0-ios   idiom-neutral UIKit infrastructure + view controllers shared by phone and pad
  Wino.Mail.UIKit.Phone/         net10.0-ios   iPhone shell: tab bar, navigation stacks, page registry (push/sheet hosts)
  Wino.Mail.UIKit.Pad/           net10.0-ios   iPad shell: split view, toolbar, sidebar + mode card, page registry (pane hosts)
  Wino.Mail.iOS/                 net10.0-ios   app head: AppDelegate, SceneDelegate, Composition, Info.plist, entitlements, assets
controls/
  Wino.Presentation.Core/        net10.0       Bindings.cs (moved), style tokens, avatar palette, theme surface enum
  Wino.Presentation.AppKit/      (keeps Layout, WinoIcons, WinoStyle colours/fonts, zone views)
  Wino.Presentation.UIKit/       net10.0-ios   UIKit twins: WinoLayout, WinoIcons (CoreText + UIFont/UIImage), WinoStyle colours, WinoZoneView
  Wino.Mail.Controls.UIKit/      net10.0-ios   mail row, folder row, avatar, pills, chips, info bar, settings cards, calendar surface, task rows
  Wino.Editor.WebKit/            net10.0-macos;net10.0-ios   renamed Wino.Editor.AppKit (it uses no AppKit symbols)
WinoMail.iOS.slnx                 new solution; WinoMail.MacOS.slnx gains the renamed/new shared projects
build/Wino.iOS.targets            iOS build policy
```

Reference rules: `Wino.Platform.*` never references presentation projects. `Wino.Mail.UIKit.Phone/Pad` reference `Wino.Mail.UIKit`, never each other. Business rules move down into `Wino.Core.Domain`, `Wino.Services`, `Wino.Mail.ViewModels` or `controls/Wino.Mail.Controls.Core`, never into an Apple project (roadmap rule: no new generic UI framework, no `Wino.Platform.Core`).

## Phase 0: shared-code prerequisites (both apps benefit, no iOS code yet)

These are the roadmap "Move" items an iOS head would otherwise copy a third time, plus the seams the Android investigation identified. Each lands with Windows Debug x64 + Release gate and Mac arm64 build.

1. **Shared ViewModel registration** (roadmap Phase 2, #25): move the ViewModel half of `src/Wino.Mail.MacOS/Infrastructure/MacViewModelRegistration*.cs` into one `RegisterWinoViewModels()` extension in `Wino.Mail.ViewModels` (lifetimes as in WinUI `App.xaml.cs`). Mac and WinUI roots call it; the Mac partials keep only view-controller registrations.
2. **Navigation core**: extract the neutral part of `AppKitNavigationService` (history stack, `IConfirmBackNavigation`/`IBackNavigationAware`, AccountCreated → `SynchronizeCreatedAccountAsync`, AccountRemoved → Welcome, `IsModeAvailable`, "setup owns the window" guard) into a `NavigationCoordinator` in `Wino.Mail.ViewModels`; `AppKitNavigationService` becomes a host adapter over it.
3. **Notification response handling**: `MacNotificationResponseHandler` already reduces input to a neutral `Response` record. Move the handling (Join/Snooze/Open, mail actions, inline reply with alias resolution) to a `NotificationResponseService` in `Wino.Services`; the `UNUserNotificationCenterDelegate` moves to `Wino.Platform.Apple` and calls it.
4. **Activation router**: neutral logic of `MacActivationCoordinator` (file grouping, has-accounts gates, mailto/webcal/billing routing) into `Wino.Mail.ViewModels`.
5. **Startup/shutdown order**: `AppDelegate.StartAsync`/`QuitAsync` sequence (runtime → theme → hooks → accounts ? shell : welcome) as an `ApplicationLifecycleCoordinator` in `Wino.Mail.ViewModels`.
6. **Presentation rules copied from WinUI** into Domain/Controls.Core: theme catalog from `MacWinoThemeService` (#50), `MailOperationPresentation`, `FolderOperationPresentation`, `ShellPaneRows.FolderGlyph` and menu-tree rules (#30/#52), `MailFilterPresentation`, provider icon map, `WinoAccountErrorText`, `SettingsPageCatalog` visibility rules, `MailRowMapper`/`CalendarTileMapper` formatting, `CalendarLayoutCalculator` with a neutral rect (#44), `WinoMailRowMetrics`, `WinoIntelligenceHeaderState`, avatar palette/initials helper, readable-text-colour formula (#11).
7. **Seams**: 
   - `IGoogleAuthorizationCodeReceiver` so `WinoGmailCodeReceiver` (loopback `TcpListener`) is one implementation; iOS supplies a custom-scheme receiver.
   - Bounded `IApplicationRuntime.SynchronizeOnceAsync(CancellationToken)` entry point beside the desktop timer loops, for BGAppRefreshTask.
   - `Microsoft.Identity.Client.Broker` in `src/Wino.Core/Wino.Core.csproj` becomes conditional on the Windows TFM (it is WAM-only).
   - `SignatureService.cs:36` platform name via `IAppMetadataService`/capabilities instead of `OperatingSystem.IsMacOS()`.
8. **`controls/Wino.Presentation.Core`**: move `Bindings.cs` (no AppKit symbols; tests already compile it by link, update `tests/Wino.Mail.Controls.Tests` to reference the project), `WinoStyle` tokens, `WinoThemeSurface` enum. `Wino.Presentation.AppKit` references it.

## Phase 1: Apple shared layer

1. **Create `src/Wino.Platform.Apple`** (multi-target). Move from `Wino.Platform.MacOS`, renaming `MacOS*`/`Mac*` prefixes to `Apple*`: `MacOSPaths`, `MacOSConfigurationService`, `MacOSFileService`, `MacOSApplicationResourceResolver`, `Security/*` (Keychain store, secret protector, exceptions), the three credential/token stores, `MacAppMetadataService`, `MacAppStoreClient`, `MacNotificationCategories`, `MacStatePersistenceService`, `MacReaderRuntimeService` (fix the `IsMacOS()` guard to `IsMacOS() || IsIOS()`), `MacNotificationBuilder` core with two injected hooks (`INotificationImageConverter` for PNG attachment copies, `IBadgePresenter`) and `MacNotificationSounds` core with an `INotificationSoundPreview` hook. `[SupportedOSPlatform]` gains `"ios"`. `MacOSApplicationIdentity` becomes a constructor argument (`AppleApplicationIdentity`) since the bundle ID feeds Keychain service names.
2. **Keep in `Wino.Platform.MacOS`** (AppKit-only): clipboard, external launcher, store/rating launcher, Shift-key shortcut service, startup integration, presence provider, global hotkey, quarantine policy, system trust anchors + `MacSecureMimeContext`/`MacSmimeCertificateService`, the AppKit notification hooks, sound preview. It references `Wino.Platform.Apple`.
3. **Rename `Wino.Core.MacOS.Bindings` → `Wino.Core.Apple.Bindings`**, multi-target. Sparkle items and `Sparkle/**` compile only for the `-macos` TFM (existing `WinoMacDistribution` condition stays). `build-native.sh` gains iOS: `-target arm64-apple-ios17.0` (iphoneos SDK) and `arm64-apple-ios17.0-simulator` slices, shallow framework layout with `MinimumOSVersion`, and `xcodebuild -create-xcframework` with three `-framework` inputs. `WinoStoreKit2.swift` wraps the review call in `#if os(iOS)` using `AppStore.requestReview(in: UIWindowScene)`. Update README.
4. **Mac head and `Composition.cs`** switch to the new names; Mac Debug arm64 build must stay green. No behaviour change.

## Phase 2: UIKit presentation and controls

1. **`controls/Wino.Presentation.UIKit`**: `WinoLayout` (UIStackView/anchors, same method names as AppKit `Layout.cs`), `WinoIcons` (CoreText registration from the bundle `Fonts/` folder, `UIImage` template rendering, `WinoIconView : UIView` redrawing on trait change), `WinoStyle` colours/fonts (UIColor semantic colours, dynamic providers), `WinoZoneView`/`WinoSurfaceView`, `ThemeBackdropView`, `WinoAccessibility`.
2. **`controls/Wino.Mail.Controls.UIKit`**: UIKit twins of the AppKit control folders with the same C# surface and the models now in Controls.Core: `WinoMailRowCell` (UITableViewCell, swipe actions supplied by the page), `WinoShellRowCell`, `WinoAccountIconView`, `WinoContactPicture`, `WinoUnreadPill`, `WinoChipView`, `WinoPivotBar` (segmented), `WinoSearchScopeBar`, `WinoInfoBar`, `WinoSettingsCard`/`Expander`/`Group` + `SettingsBinder`, `WinoModeSwitcher` card (iPad), `WinoMiniCalendarView`, `WinoCalendarSurfaceView` over the shared layout calculator, `WinoTaskRowCell`, `WinoBriefingCardView`, `WinoStateView`.
3. **`controls/Wino.Editor.WebKit`**: rename `Wino.Editor.AppKit`, multi-target, drop unused `using AppKit`. `MacOsMailDocumentBuilder` renamed `AppleMailDocumentBuilder`. iOS WKWebView creation, keyboard accessory and print formatter stay in the head.

## Phase 3: iOS head, platform services, composition

1. **`src/Wino.Platform.iOS`**: `IOSClipboardService` (UIPasteboard), `IOSExternalLauncher` (UIApplication.OpenUrl; files via QuickLook/`UIDocumentInteractionController` through a presenter callback), `IOSAttachmentPlatformService` (no quarantine), `IOSMicrosoftStoreService` (rating via `itms-apps://…?action=write-review`, reuse throttle logic), `IOSOutlookAuthenticationHost` (MSAL `WithIosKeychainSecurityGroup`, redirect `msauth.com.winomail.ios://auth`, `WithParentActivityOrWindow`, no `MsalCacheHelper`), `IOSGoogleAuthorizationCodeReceiver` (ASWebAuthenticationSession, custom scheme), badge via `UNUserNotificationCenter.SetBadgeCount`, `UIImage` PNG converter, AudioToolbox sound preview, stubs returning Unavailable for startup integration, presence, Shift-key, S/MIME.
2. **`src/Wino.Mail.UIKit`** (idiom-neutral): `UIKitDispatcher`, `WinoViewController<TViewModel>` (same activate/release/BindingScope lifecycle as Mac), `UIKitDialogService : IMailDialogService` (UIAlertController, UIDocumentPicker for pick/save, sheet-presented pickers for folders/accounts/calendars/categories, shared "don't ask again" rule from Phase 0), `UIKitNavigationService` over the shared `NavigationCoordinator` with a `UIKitPageHost` enum (`Root`, `ShellContent`, `RenderingFrame`, `Push`, `Sheet`, `FullScreenSheet`) and a registry interface the phone/pad projects implement, `UIKitThemeService : INewThemeService` (`overrideUserInterfaceStyle`, tint, wallpaper backdrop, catalog from Domain), `UIKitUnderlyingThemeService`, `UIKitSenderPictureProvider`, `UIKitMailPrintPresenter` (UIPrintInteractionController + `WKWebView.CreatePdf`), `ShortcutKeys` for `UIKeyCommand`, and the idiom-neutral view controllers: onboarding pages, Settings pages (Windows group/name/order, minus Companion, Backup, startup, hotkeys, hover/density/reading-pane options), compose (editor + recipients + format toolbar as input accessory), reader, event details/compose, contact edit, daily briefing, What's New, Wino Account sheets, Debug bridge (file commands + view snapshots in Simulator).
3. **`src/Wino.Mail.UIKit.Phone`**: `PhoneShellViewController` (UITabBarController, Mail · Calendar · Contacts · To Do), Mail flow Mailboxes → folder list → message as `UINavigationController` stacks, reader bottom toolbar (Archive, Delete, Move, Flag, Reply menu, More), leading swipe Archive, trailing Delete/Flag/More, long-press menus, Focused/Other segmented control, compose/move/briefing as sheets, avatar → Settings full-screen sheet, phone page registry. Calendar (day/agenda + month picker), Contacts (indexed list → card), To Do (lists → tasks → detail) per the canvas.
4. **`src/Wino.Mail.UIKit.Pad`**: `PadShellViewController` (UISplitViewController three-column: 260 sidebar with Windows rows + mode switcher card at foot, 360 list, reader with command bar), `UINavigationBar`/toolbar row (sidebar toggle, sync + title, centred search, briefing, What's New, avatar → Settings sheet), popovers for Move/filters/account, pad page registry, `UIKeyCommand` shortcuts (Cmd+1..4, Cmd+N, Cmd+R…), iPad multi-scene for pop-out reader/compose (`AdditionalWindows=true`). Never the floating top tab bar. Calendar week grid, Contacts list + card, To Do list + detail drawer in Windows layout.
5. **`src/Wino.Mail.iOS`**: `AppDelegate` (UNUserNotificationCenter delegate from Platform.Apple, BGTaskScheduler registration calling `SynchronizeOnceAsync`, remote-notification registration only), `SceneDelegate` (idiom pick, URL contexts → activation router, state restoration), `Composition.cs` mirroring the Mac root with iOS services and `PlatformCapabilities(Printing: true, PdfExport: true, Smime: false, AdditionalWindows: isPad, Notifications: true, NotificationActions: true, StartupIntegration: false, Tray: false, GlobalHotkeys: false, GeneralActivation: true, AppleAppStore: true)`, `Info.plist` (URL schemes mailto/wino/webcal/webcals + `msauth.com.winomail.ios` + Google scheme, `LSApplicationQueriesSchemes` msauthv2/msauthv3, document types ics/vcf/eml, `UIBackgroundModes` fetch/processing/remote-notification, `BGTaskSchedulerPermittedIdentifiers`, `UIDeviceFamily` 1,2), `Entitlements.plist` (keychain-access-groups incl. `com.microsoft.adalcache`, aps-environment), `Assets.xcassets` from the Mac icon set, bundle resources: WinoIcons fonts, theme wallpapers, What's New JSON/PNG, ML models (`WinoBundleModelAssets` twin; verify `AppContext.BaseDirectory` in the flat `.app`). `#if WINO_APPSTORE` always defined; `IAppStoreClient` registered.

## Build interface

- `Directory.Build.props`: no change to host defaults (a Mac host still resolves `MacOS`; iOS builds pass `-p:WinoTargetPlatform=iOS` explicitly, as the AGENTS rule already requires for non-default targets).
- `Directory.Build.targets`: validation accepts `iOS`; the Release AOT gate exemption matches `-ios` as well as `-macos`; import `build/Wino.iOS.targets` when `WinoTargetPlatform == iOS` or TFM contains `-ios`.
- `build/Wino.iOS.targets`: `IsAotCompatible=false`, `PublishAot=false` for `-ios` TFMs; `MtouchLink`/trimming defaults per configuration; `RuntimeIdentifier` defaults (`iossimulator-arm64` Debug, `ios-arm64` Release) when unset.
- `Wino.Mail.ViewModels` and `Wino.Mail.Controls.Core` TFM conditions already fall to `net10.0` for non-Windows; nothing to change.
- `Wino.Core.ML`: iOS head gets a direct `PackageReference Include="Microsoft.ML.OnnxRuntime"` (its xcframework comes from non-transitive `build/` targets). If static linking fails in the first simulator build, add an iOS selector that defines `WINO_ML_UNSUPPORTED` (same pattern as `osx-x64`).
- `Directory.Packages.props`: no new packages expected beyond those already listed (MSAL, SkiaSharp, sqlite-net-pcl all ship iOS assets). Verify SQLitePCLRaw provider selection on first restore.
- `WinoMail.iOS.slnx` with folders `/iOS/`, `/Apple/`, `/Tests/`; `WinoMail.MacOS.slnx` updated for renamed projects.
- Workspace `WinoMail.iOS.code-workspace` with build tasks (simulator arm64 Debug) and a launch config; `scripts/release/ios/build-ios-appstore.sh` later, modelled on `build-macos-appstore.sh`.

## Documentation

- `docs/ios-design-decisions.md`: canvas decisions (from memory + canvas), what differs from Mac Revision 2.
- `docs/harness/development-ios.md`: requirements (`dotnet workload install ios`, Xcode 27, simulator), build commands, identity, Keychain groups, verification table.
- `AGENTS.md`: platform table row for iOS, routing bullet for iOS changes, shared-change rule now names three apps.
- `docs/README.md`, `docs/harness/implementation-rules.md` architecture tree, `scripts/README.md`.
- `src/Wino.Core.Apple.Bindings/README.md` iOS slice instructions.

## Verification

Per phase, on the available host. Windows host today; Mac steps are the handoff to `burak-mac` (see memory `macos-build-over-ssh`).

| Phase | Evidence |
| --- | --- |
| 0 | Windows Debug x64 build + Release x64 gate; `dotnet test` for Core, ViewModels, Controls tests; Mac Debug arm64 build; manual Mac smoke (navigation, notifications actions, activation) since behaviour moved |
| 1 | Mac Debug arm64 + x64 builds of `WinoMail.MacOS.slnx`; `./build-native.sh StoreKit2` produces a three-slice xcframework (`plutil -p Info.plist` shows macos, ios, ios-simulator); App Store variant build with `WinoMacDistribution=AppStore`; Mac launch + Keychain read of existing accounts (no data migration regressions) |
| 2–3 | `dotnet build src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Debug -r iossimulator-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true`; launch in Simulator (`xcrun simctl`), onboarding with the lab IMAP servers (five-server lab reachable from the Mac), Debug bridge snapshots for phone and pad |
| 4–5 | Manual acceptance per canvas board on iPhone and iPad simulators: light/dark, Dynamic Type, rotation, Split View/Stage Manager, hardware keyboard, VoiceOver; device build signed with Apple Development |
| Release | `dotnet publish -c Release -r ios-arm64` with App Store entitlements, `codesign --verify`, TestFlight upload via `~/asc-tool/asc.py` only when asked |

A build alone does not prove runtime behaviour; manual checks are reported as pending until performed.

## Items requiring the user (not blocking Phases 0–2)

- Register `com.winomail.ios` App ID, Apple Development profile and App Store record (team 4VB7YWRAQ9).
- Azure app registration: add redirect `msauth.com.winomail.ios://auth`.
- Google Cloud console: create an iOS OAuth client for `com.winomail.ios` (loopback is deprecated for iOS clients).
- Decide later: APNs relay wiring, Share Extension, widgets.

## Execution order

Phase 0 → 1 (Mac stays green throughout) → 2 → 3 (first simulator launch to onboarding) → 4 phone Mail → 5 pad Mail → 6 Calendar/Contacts/To Do on both idioms → 7 Settings parity, store, notifications, background refresh → docs and release script. Each phase is a reviewable checkpoint; nothing is committed unless asked.
