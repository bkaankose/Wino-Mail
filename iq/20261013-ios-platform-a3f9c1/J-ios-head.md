# J: iOS head, UIKit layer, phone and pad shells (Phase 3 items 2–5, T14)

**Tree:** `D:\Wino-Mail`, branch `feature/iOS`, main working tree. Nothing is committed or pushed.
- New projects are untracked.
- `git mv` staged the handler move.
- The other edits are unstaged.

**Result:**
- Both idioms launch in the iOS 27.0 Simulator to the Welcome page without crashing.
- The Wino Mail logo and the WinoIcons glyphs render, and the console shows the runtime started.
- All requested builds end with 0 errors.

## Projects

`src/Wino.Mail.UIKit/Wino.Mail.UIKit.csproj`:
```xml
<TargetFramework>net10.0-ios</TargetFramework> <SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>
<WinoTargetPlatform>iOS</WinoTargetPlatform> <Nullable>enable</Nullable> <ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors Condition="Release">true</TreatWarningsAsErrors>
ProjectReference: Wino.Mail.ViewModels, Wino.Core, Wino.Services, Wino.Platform.Apple, Wino.Platform.iOS,
  controls/Wino.Presentation.Core, Wino.Presentation.UIKit, Wino.Mail.Controls.UIKit, Wino.Editor.WebKit
PackageReference: Microsoft.Extensions.DependencyInjection
```

`src/Wino.Mail.UIKit.Phone` and `src/Wino.Mail.UIKit.Pad` have the same shape. Each references only `Wino.Mail.UIKit`; neither references the other.

`src/Wino.Mail.iOS/Wino.Mail.iOS.csproj` (key lines):
```xml
<OutputType>Exe</OutputType> <TargetFramework>net10.0-ios</TargetFramework> <SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>
<WinoTargetPlatform>iOS</WinoTargetPlatform> <ApplicationTitle>Wino Mail</ApplicationTitle> <ApplicationId>com.winomail.ios</ApplicationId>
<PublishAot>false</PublishAot> <EnableDefaultCodesignEntitlements>false</EnableDefaultCodesignEntitlements>
<RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == '' and '$(Configuration)' == 'Release'">ios-arm64</RuntimeIdentifier>
<PropertyGroup Condition="$(RuntimeIdentifier.StartsWith('ios-'))">  <!-- device only -->
  <CodesignEntitlements>Entitlements.plist</CodesignEntitlements>
  <CodesignKey Condition="'$(CodesignKey)' == ''">Apple Development</CodesignKey>
  <CodesignProvision Condition="'$(CodesignProvision)' == ''">Wino Mail iOS Development</CodesignProvision>
</PropertyGroup>
Version from Package.appxmanifest (same regex as the Mac); DefineConstants WINO_APPSTORE
ProjectReference: Wino.Mail.UIKit, .Phone, .Pad, Wino.Platform.iOS, Wino.Core.Apple.Bindings
PackageReference: Microsoft.Extensions.DependencyInjection, Serilog.Extensions.Logging, Microsoft.ML.OnnxRuntime
BundleResource: Fonts/WinoIcons*.ttf, WinoLogo.png, Themes/*.jpg (minus Acrylic/Mica); None Assets/WhatsNew/*.json|png
Target WinoBundleModelAssets (Mac twin: Models/ and Assets/ as PublishFolderType=Assembly)
```

- **TFM:** every new project uses unversioned `net10.0-ios`. The app resolves 27.0 and the libraries resolve 26.0; this built and ran.
- **Simulator Debug runtime:** no `MtouchInterpreter` was needed. The SDK default ran.
- **ONNX:** the direct `Microsoft.ML.OnnxRuntime` reference links statically (`nm` shows `OrtGetApiBase` in the executable). No `WINO_ML_UNSUPPORTED` selector was needed, and `Wino.Core.ML` is unchanged. Magika was not exercised at runtime.
- **Bundle layout:** `Models/Magika/...`, `Assets/WhatsNew/...`, `Fonts/`, `Themes/` and `WinoLogo.png` all sit at the `.app` root (= `AppContext.BaseDirectory`). The twin target keeps that layout explicit.
- **Bundle name:** `Wino.Mail.iOS.app` (the assembly name); the display name is "Wino Mail".
- **Solution:** `WinoMail.iOS.slnx` adds the head, `Wino.Mail.UIKit`, `.Phone`, `.Pad` and `Wino.Platform.iOS` (under `/iOS/`), plus `Wino.Platform.Apple` and `Wino.Core.Apple.Bindings` (under `/Apple/`).

## Registry and host interfaces (`Wino.Mail.UIKit/Infrastructure`)

**`UIKitPageHost`** has six values: `Root`, `ShellContent`, `RenderingFrame`, `Push`, `Sheet`, `FullScreenSheet`.

**`IUIKitPageRegistry`** has `TryGet(page, parameter, out Type, out UIKitPageHost)` and the overload without a parameter.

**`UIKitPageRegistry`** (abstract base):
- `Register<TController>(page, host)` and `RegisterHostResolver(page, Func<object?, UIKitPageHost?>)`.
- It registers the onboarding routes for both idioms. `ImapCalDavSettingsPage` is a `Sheet`, resolved to `Root` when adding an account.
- `ToHostKind` maps to the coordinator's host kinds:

  | UIKit host | Coordinator `NavigationHostKind` |
  | --- | --- |
  | `Root` | `Window` |
  | `ShellContent` | `ShellContent` |
  | `RenderingFrame`, `Push` | `RenderingFrame` |
  | `Sheet`, `FullScreenSheet` | `Settings` (one presented stack) |

- `PhonePageRegistry` and `PadPageRegistry` are empty subclasses for now.

**`IUIKitShellHost`**, implemented by `UIKitShellHostBase`, then `PhoneShellHost` and `PadShellHost`:
- `Window`, `WindowReady`, `AttachWindow`
- `ShowRoot` (cross-fade, dismisses leftover sheets)
- `CreateShell`
- `ShowInRenderingFrame(shell, vc)`: the phone pushes; the pad uses the reader column.
- `Push(shell, vc)`: onto the open sheet, else onto the shell's current stack.
- `PresentSheet(vc, style, dismissedByUser)`: page sheet on the phone, form sheet on the pad, full screen for `FullScreenSheet`. Swipe-dismiss is reported through `UIAdaptivePresentationControllerDelegate`.
- `DismissSheetAsync`, `GoBack(shell)`, `TopViewController`.

**`UIKitShellViewController`** (base of the phone and pad shells, a `WinoViewController<WinoAppShellViewModel>`):
- Mode activation copied from the Mac `ActivateModeAsync`. Mail awaits `InitializeNavigationAsync`; other modes call `ActivateShellMenu`.
- `SetContentAsync` and `ClearContentAsync`, built on the abstract `PlaceContent`, `ModeChanged`, `MenuChanged` and `CurrentNavigationController`.
- Helpers: `RequestMode`, `OpenSettings` (Settings mode → sheet), `SelectMenuItem`, `SynchronizeAsync`.

**`UIKitNavigationService : INavigationService, INavigationHost`** runs over `NavigationCoordinator`:
- **Root pages:** replace the window root. While the shell is up they open in an add-account sheet with the navigation bar hidden; a swipe-dismiss calls `OnSecondaryWindowClosedByUser`.
- **Sheet pages:** share one presented navigation stack.
  - `TrySettingsGoBack` pops it, and `CloseSettingsAsync` dismisses it and releases its pages.
  - `IsSettingsAvailable` means `SettingsPage` is registered or a sheet is open.
- **Rendering frame:** the previous rendering-frame page is released when replaced.

**`WinoViewController<T>`:**
- Same members as the Mac: `IWinoViewController`, `IViewModelHost`, `BindingScope`, `BindText`, `Bind`, `CommandButton`.
- A page also releases itself in `ViewDidDisappear` when it is popped or its sheet is dismissed, so back swipes need no router call.

## Composition (`src/Wino.Mail.iOS/Composition.cs`)

**Registration order:**
1. `IOSPlatformBootstrap.Create(new AppleApplicationIdentity("com.winomail.ios"))`.
2. `AddLogging` → Serilog.
3. `RegisterSharedServices`, then `RegisterCoreServices`.
4. `AddIOSPlatformServices(bootstrap, Presenter)`. `Presenter` is a closure over the built provider's `UIKitAuthenticationPresenter.Presenter()`, the top-most window controller. `IAuthenticatorConfig` stays `MailAuthenticatorConfiguration`; a comment says the Google iOS client ID is a user action.
5. `RegisterWinoViewModels()`.
6. `RegisterUIKitServices(dispatcher)`: `UIKitNavigationService`/`INavigationService`, `UIKitApplicationShellHost`, `ActivationRouter`, `UIKitActivationCoordinator`, `NotificationResponseNavigator`, `ApplicationLifecycleCoordinator`, `UIKitApplicationLifecycleHost`, `UIKitDialogService`, `CustomThemeFileStore`, `UIKitThemeService`/`INewThemeService`, `UIKitUnderlyingThemeService`, `UIKitSenderPictureProvider`, `UIKitAuthenticationPresenter`, `IIOSFilePresenter → UIKitFilePresenter`.
7. `RegisterUIKitViews()`: the five onboarding controllers.
8. `AddPadShell()` or `AddPhoneShell()`, chosen by `UIDevice.UserInterfaceIdiom`.
9. `IApplicationLifecycleHook`s, run in order:
   - Notifications: `RegisterCategories`, attach the response delegate, start the reminder server, update the badge.
   - Activation: flush queued URLs.
   - Background refresh: schedule.
   - Debug bridge: Debug builds only.

**`AppDelegate.FinishedLaunching`:**
- Sets `UNUserNotificationCenter.Delegate = AppleNotificationResponseHandler`.
- Registers the BGTask handlers: `com.winomail.ios.refresh` and `.processing` → `IApplicationRuntime.SynchronizeOnceAsync(ct)`.
- Calls `WinoIcons.Register()`, then composes the container and starts `ApplicationLifecycleCoordinator.StartAsync(UIKitApplicationLifecycleHost)`.
- Calls `RegisterForRemoteNotifications()`. Registration only.
- `WillTerminate` runs `StopAsync`, bounded to 3 seconds.

**`SceneDelegate`:**
- Creates the window over a tracked `ThemeBackdropView` and calls `IUIKitShellHost.AttachWindow`. `ShowStartupAsync` waits for `WindowReady`.
- URL contexts go to `UIKitActivationCoordinator`, which queues them until ready. msauth and Google callback schemes are skipped because the web-auth session receives them.
- `sceneDidEnterBackground` schedules a refresh.

**Shared code moved:** `MacNotificationResponseHandler` became `src/Wino.Platform.Apple/Services/AppleNotificationResponseHandler.cs` (`git mv`).
- It is unchanged apart from the namespace, the `[SupportedOSPlatform]` attributes and its three forwarders becoming public.
- The Mac `AppDelegate.Notifications.cs` and a comment in `MacViewModelRegistration.Notifications.cs` use the new name.

## Info.plist and entitlements

**Info.plist:**
- `CFBundleIdentifier com.winomail.ios`, `MinimumOSVersion 17.0`, `UIDeviceFamily` 1,2, `UILaunchScreen`.
- Scene manifest: `SceneDelegate`, `UIApplicationSupportsMultipleScenes=false`. iPad multi-scene comes later.
- Orientations: portrait and landscape on iPhone, all four on iPad.
- `XSAppIconAssets` points at the asset catalog. The icon is a single 1024 image: the Mac `icon_512x512@2x` cropped to its 824 artwork, still with alpha.
- `CFBundleLocalizations` as on the Mac.
- `CFBundleURLTypes`: mailto, wino, webcal/webcals, `msauth.com.winomail.ios`, and `com.googleusercontent.apps.REPLACE-WITH-IOS-CLIENT-ID` (placeholder).
- `LSApplicationQueriesSchemes`: msauthv2, msauthv3.
- Document types: ics, vcf, eml.
- `UIFileSharingEnabled`: Documents shows in the Files app; see `PickFilePathAsync`.
- `UIBackgroundModes`: fetch, processing, remote-notification. `BGTaskSchedulerPermittedIdentifiers`: the two IDs above.
- `ITSAppUsesNonExemptEncryption=false`.

**`Entitlements.plist`:**
- `keychain-access-groups`: `$(AppIdentifierPrefix)com.winomail.ios` and `$(AppIdentifierPrefix)com.microsoft.adalcache`.
- `aps-environment`: development.
- **Applied to device builds only.** With them, the SDK required a provisioning profile even for the simulator ("Could not find any available provisioning profiles"). The SDK also picks up `Entitlements.plist` by convention, so `EnableDefaultCodesignEntitlements=false` and the property is set only for `ios-*` RIDs.
- **Simulator consequences:**
  - The app log shows "no valid aps-environment entitlement".
  - BGTaskScheduler reports "not available on this platform", which is expected in the Simulator.
  - MSAL's `com.microsoft.adalcache` keychain group is not available, so Outlook sign-in in the Simulator may fail with -34018 until it is tested signed on a device.

## Dialog service (`UIKitDialogService`)

**Real:**
- **Prompts:** `ShowConfirmationDialogAsync`, `ShowMessageAsync`, `ShowThreeButtonDialogAsync`, `ShowWinoCustomMessageDialogAsync` ("Don't ask again" is a button that stores the key), `ShowHardDeleteConfirmationAsync`, `ShowTextInputDialogAsync` (null on cancel).
- **Wino Account and IMAP:** `ShowWinoAccountSyncSecretDialogAsync` (secure field, plus a confirm field for a new backup), `ShowWinoAccountExportDialogAsync`, `ShowServerCertificateTrustDialogAsync` (fails closed), `ShowImapValidationFailedDialogAsync` (log tail plus Copy diagnostics).
- **Banners:** both `InfoBarMessage` overloads, `ShowNotSupportedMessage` and `ShowReadOnlyCalendarMessage`, as a `WinoInfoBar` banner on the window that dismisses itself.
- **Files:**
  - `PickWindowsFolderAsync`, `PickWindowsFileContentAsync`, `PickFilesAsync` and `PickFilesMetadataAsync` use `UIDocumentPickerViewController`.
  - `PickFilePathAsync` returns a non-clobbering path in Documents; iOS has no save panel that returns a path.
- **Pickers** (searchable sheet, tree indent): `ShowMoveMailFolderDialogAsync`, `PickFolderAsync`, `ShowAccountPickerDialogAsync`, `ShowSingleCalendarPickerDialogAsync` (`NavigateToSettings` is always false), `ShowContactDestinationPickerDialogAsync`, `ShowTaskListPickerDialogAsync`.
- **Message source:** `ShowMessageSourceDialogAsync`.
- **Account creation:** `GetAccountCreationDialog`, a medium-detent progress sheet with Cancel.

**Stubbed** (cancelled or null result, plus the unsupported banner):
- `HandleSystemFolderConfigurationDialogAsync`, `ShowNewContactListDialogAsync`, `ShowAccountReorderDialogAsync`
- `ShowSignatureEditorDialog`, `ShowCreateAccountAliasDialogAsync` (alias null), `ShowEditMailCategoryDialogAsync`
- `ShowKeyboardShortcutDialogAsync` (`Canceled()`)
- `ShowWinoAccountRegistrationDialogAsync`, `ShowWinoAccountLoginDialogAsync`, `ShowUnlimitedAccountsPurchaseChannelDialogAsync`
- `ShowAccountProviderSelectionDialogAsync` returns null, as on the Mac.
- `UIKitApplicationShellHost.OpenMailFileAsync` returns false: there is no .eml viewer yet.

## Other infrastructure

- **`UIKitDispatcher`:** runs work on the main queue, inline when already on the main thread.
- **`UIKitThemeService`:** a full port of the Mac service.
  - Applies `overrideUserInterfaceStyle`, window tint and `WinoStyle.AccentOverride`, `HasBackdrop` and `WinoThemeSurfaces`.
  - Paints tracked `ThemeBackdropView`s through `UIKitThemeService.Track`.
  - Custom themes go through `UIKitThemeImaging`.
  - The window-material choice is always None.
- **Presenters:** `UIKitUnderlyingThemeService`, `UIKitSenderPictureProvider`, `UIKitMailPrintPresenter` (`UIPrintInteractionController` plus `WKWebView.CreatePdfAsync`), `UIKitFilePresenter` (QuickLook), `UIKitAuthenticationPresenter`.
- **`UIKitDebugBridge`** (Debug only):
  - Polls `<app tmp>/wino-debug/cmd`. In the Simulator that is `$(xcrun simctl get_app_container <udid> com.winomail.ios data)/tmp/wino-debug/cmd`.
  - Commands: `snap NAME`, `mode`, `page`, `settings`, `back`, `theme`, `appearance`, `iconstyle`, `sleep`.
  - Writes `out.log` and `NAME.png`.
- **Shells:**
  - **iPhone:** `PhoneShellViewController`, a child `UITabBarController` with four navigation stacks and WinoIcons tab items.
    - The Mail tab's root is `MailboxesPlaceholderViewController`: read-only `WinoShellRowCell` rows built from the shell menu by the shared `Shell/ShellMenuRows`.
    - The other tabs show a zone placeholder.
    - Every stack has the avatar button, which opens Settings.
  - **iPad:** `PadShellViewController`, a child triple-column `UISplitViewController` (260/360).
    - Sidebar: `ShellMenuRows` (taps invoke items) with a `WinoModeSwitcher` card at its foot.
    - The list and reader columns hold zone placeholders over the backdrop.
    - Toolbar: sidebar toggle, sync and title over the list; search, briefing, What's New and avatar over the reader.
    - Cmd+1…4 `UIKeyCommand`s.
- **Strings:** added `IOS_MailboxesTitle` ("Mailboxes") to `en_US/resources.json`. No other new strings; other locales are untouched.

## Verification

Logs are in `artifacts/ios-platform/J-*.log`.

| Step | Result |
| --- | --- |
| Mirror `~/Desktop/wt-J` + xcframework swap | Done. StoreKit2 has three slices. |
| Mac head Debug osx-arm64 (after the handler move) | **0 errors, 54 warnings**, the T8/I baseline (`J-mac-debug-arm64.log`). |
| `Wino.Mail.iOS` Debug `-r iossimulator-arm64` | **0 errors, 8 warnings**. All 8 are IL2104 trim notices on third-party and BCL assemblies; one CA1422 in my code was fixed (`J-ios-debug-sim.log`). |
| `WinoMail.iOS.slnx` Debug | **0 errors, 25 warnings**. All pre-existing: IL2104, the Platform.Apple nullable set from F, and xUnit2031 (`J-ios-slnx-debug.log`). |
| Windows Release gate (WinUI x64, shared code touched) | **0 errors, 0 warnings** (`J-windows-release-x64.log`). |
| iPhone 18 Pro, iOS 27.0 | Launched; the console shows `scene connected (Phone)` and `runtime started, showing Welcome`. Screenshots: `artifacts/ios-platform/J-iphone-welcome.png`, and `J-iphone-provider.png` (debug bridge `page ProviderSelectionPage` + `snap`; the provider tiles render with brand glyphs). |
| iPad Pro 11-inch (M5), iOS 27.0 | Launched; `scene connected (Pad)`, `runtime started, showing Welcome`. Screenshot: `artifacts/ios-platform/J-ipad-welcome.png`. |
| App log (`J-iphone-wino.log`) | No errors. Language, runtime, reminder and refresh info lines only. |

**Defects found and fixed on the iPad launch** (no crash occurred):
- The Welcome column stretched its first tile.
- The description label lost its second line.

**Fixes:**
- A centring container at least as tall as the zone, instead of a low-priority centre constraint.
- A second layout pass that sets `PreferredMaxLayoutWidth`.
- The progress page uses the same container pattern.

**Not verified (pending manual checks):**
- Onboarding against the lab IMAP servers.
- Outlook and Google sign-in.
- The shell after an account exists: the phone tabs, the iPad split view, the toolbar and Cmd+1…4.
- Light and dark mode, themes, rotation, VoiceOver, notifications and background refresh on a device.
- A device-signed build: there is no `com.winomail.ios` App ID or profile yet.

## Deviations

- **Logs:** the log path is the shared coordinator's `<ApplicationData>/Logs/wino*.log`, which on iOS is `Library/Application Support/com.winomail.ios/Logs`, not `Library/Logs`.
- **Lifecycle host:** it is a separate `UIKitApplicationLifecycleHost`, with head hooks as `IApplicationLifecycleHook` services, rather than implemented by `AppDelegate`.
- **Idiom pick:** made in Composition (`AddPhoneShell`/`AddPadShell`). `SceneDelegate` only attaches the window.
- **Notification delegate:** moved into `Wino.Platform.Apple` as the plan intended, because F had left it in the Mac head.
- **Shared contract issue:** `INavigationAware.OnNavigatedTo/From(object)` and several `IMailDialogService` returns are declared non-nullable, but every head passes or returns null. The Mac uses `null!`; this code passes or returns null without `!`. That is silent under the repository's `Nullable=annotations`, and shows as CS8604/CS8603 only with `-p:Nullable=enable`. Proposed fix: make those signatures nullable in Domain.

## For K (phone Mail), L (pad Mail) and M (shared pages)

- **Registering a page:**
  - Add the controller to DI (`services.AddTransient<XViewController>()`): in `UIKitViewRegistration.RegisterUIKitViews` for shared pages, or in `PhoneViewRegistration`/`PadViewRegistration` for idiom pages.
  - Add the route in `PhonePageRegistry`/`PadPageRegistry`, e.g. `Register<MailListPageViewController>(WinoPage.MailListPage, UIKitPageHost.ShellContent)`.
  - Use `RegisterHostResolver` for parameter-dependent hosts.
  - Controllers derive from `WinoViewController<TViewModel>`.
- **Mode roots:** `ShellContent`.
  - Phone: the page becomes the tab stack's content. In Mail it is pushed over Mailboxes, so Inbox opens with a back button to Mailboxes; K replaces `MailboxesPlaceholderViewController` with the real Mailboxes page.
  - Pad: the page goes into the list column. The column's toolbar items are copied onto it.
  - `IsModeAvailable` turns true once `MailListPage`, `CalendarPage`, `ContactsPage` or `ToDoPage` is registered.
- **Reader frame:** register `MailRenderingPage` and `ComposePage` as `RenderingFrame`.
  - The pad host shows them in the reader column through `PadShellViewController.ShowInReader`; the previous page is released.
  - The phone host pushes them on the current tab stack.
  - `Push` always pushes: on the open sheet, else on the phone tab or pad list column.
  - The reader creates `new UIKitMailPrintPresenter(() => webView, Dispatcher)`.
- **Sheets:**
  - Register Settings pages as `FullScreenSheet` on the phone and `Sheet` on the pad. Register `WinoPage.SettingsPage` to make the avatar and `ChangeApplicationMode(Settings)` work: until then `IsSettingsAvailable` is false and the avatar does nothing.
  - Further sheet routes push inside the open sheet. Back pops it, and closing the sheet releases its pages.
  - Use `IUIKitShellHost.PresentSheet` for ad-hoc sheets.
- **Shell hooks:**
  - `UIKitShellViewController.CurrentNavigationController`, `SelectMenuItem`, `SynchronizeAsync`, `RequestMode`, `OpenSettings`.
  - `Shell/ShellMenuRows`, a `UITableViewSource` over `ShellMenu` with `Invoked`.
  - The pad sidebar already invokes menu items. The phone Mailboxes rows are read-only.
- **Backdrop and themes:** wrap any new `ThemeBackdropView` with `UIKitThemeService.Track(...)`. Content sits in `WinoZoneView`s.
- **Debug bridge:** pages can add commands with `UIKitDebugBridge.Register` inside `#if DEBUG`.
- **Launch script:** `~/Desktop/wt-J/launch-sim.sh <udid> <name> <seconds>` installs, launches with the console and takes a screenshot.
- **Simulator UDIDs:** iPhone 18 Pro `B787D2AE-9DF5-4EDE-8E97-D3AFEFC8BDDF`, iPad Pro 11-inch (M5) `21E1D099-F457-4068-99A3-2C2CEF6E035E`.
