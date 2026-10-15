# T4 Android head skeleton (done, 14 Oct 2026)

## Files

`src/Wino.Mail.Android` (in `WinoMail.Android.slnx`): csproj (`com.winomail.android`, min 23, target 36, version 2.2.2 / code 2002002 from `Package.appxmanifest`, Debug RID `android-arm64`, assembly compression on), `AndroidManifest.xml` (T3 FileProvider, Custom Tabs `<queries>`, `allowBackup=false`, INTERNET, ACCESS_NETWORK_STATE, predictive back opt-in), `MainApplication`, `MainActivity`, `AndroidComposition`, `AndroidForegroundLifecycle`, `Infrastructure/*`, `Views/Onboarding/*`. Resources: `Theme.Wino` (Material3 DayNight NoActionBar, Wino tokens: Clouds accent in light, Nighty in dark, no dynamic colour), adaptive icon (from `AppEntries/Mail.svg`, rendered with qlmanage, sips densities), XML layouts. Assets linked from WinUI: three WinoIcons fonts, `Clouds.jpg`/`Nighty.jpg`, `Assets/Providers/*.png` as packaged resources.

Shared edits: `Directory.Packages.props` (Activity, AppCompat, Fragment, Material; **WebKit 1.17.0 → 1.15.0.2**, because androidx.webkit 1.16+ needs minSdk 24 and the manifest merge failed); `resources.json` + `Android_Welcome_PrivacyDescription` (the Mac string says Keychain).

## Composition order

`AndroidPlatformBootstrap.Create` → logging → `RegisterSharedServices` → `RegisterCoreServices` → `AddAndroidPlatformServices(bootstrap, tracker.Current)` → `RegisterWinoViewModels` → head (size class, registry, navigation, shell/lifecycle hosts, `ActivationRouter`, dialogs, theme, `DeferredNotificationBuilder`). Built once in `MainApplication.OnCreate` from the application context; `PackagedResourceInstaller` copies `PackagedResources/` assets once per version. `AndroidForegroundLifecycle`: first started activity → `ApplicationLifecycleCoordinator.StartAsync` (logger/Sentry first); no started activity for 10 min → `StopAsync`, then the process ends (the shared runtime cannot restart after a stop). Owner/T11 should confirm this policy.

## Navigation

`AndroidNavigationService` implements `INavigationService`/`INavigationHost` over the shared `NavigationCoordinator`. Pages are `PageEntry` objects (ViewModel + parameter, activate once / release once) in layers: main, shell content, per-mode back stacks, secondary onboarding, Settings. The top entry is shown in the single `FragmentContainerView`; `MainActivity` calls `base.OnCreate(null)` and re-attaches, so on recreation a new fragment binds to the same ViewModel. `AndroidPageRegistry` maps `WinoPage` → fragment/ViewModel/host. Back: `OnBackPressedCallback` enabled only when the app can go back (pops mode stack, else coordinator). The shell is a placeholder page until T7.

## Size class

`IWindowSizeClassService` / `AndroidWindowSizeClassService` (compact <600, medium 600–839, expanded ≥840 dp; `CurrentWindowMetrics` on 30+, configuration below), updated in `OnCreate`/`OnConfigurationChanged`, raises `WidthClassChanged`.

## Verified

- Debug build: 0 errors, 0 warnings except the existing Pkcs NU1902/1903.
- Phone API 36: launches to Welcome, no exceptions in logcat (`T4-phone-welcome-light.png`, `T4-phone-welcome-dark.png`); Add account → placeholder (`T4-phone-provider-placeholder.png`); landscape keeps the page and back returns to Welcome (`T4-phone-landscape-provider.png`, `T4-phone-landscape-welcome.png`); dark-mode switch recreates the activity without losing the page.
- Tablet API 36: `T4-tablet-welcome-light.png`, `T4-tablet-welcome-dark.png`.
- Core tests (MacOS): 1215 passed, the 5 known failures. Mac Debug arm64 build succeeded.

Note: a Debug APK installed with `adb install` crashes (fast deployment keeps assemblies outside the APK); use `-t:Install` or `-t:Run`.

## Pending / gaps

- Dialogs not built (show "not implemented yet" and return the cancel value): file pickers, system folder setup, account order, signature, alias, category, keyboard shortcut, Wino Account sign-in/registration, new-backup passphrase, purchase channel. `ShowAccountProviderSelectionDialogAsync` returns a faulted task (no shared caller). Dialogs are dismissed (answer Cancel) on recreation.
- Theme: startup part only (night mode, wallpaper); gallery, custom themes, runtime accent → T9.
- Notifications are no-ops (T11). Activation intents, `OpenMailFileAsync` → later items.
- Windows checks (owner): Debug x64, Release gate, Core tests, for the `resources.json` change.
- Not run: API 23 emulator, Release build.
