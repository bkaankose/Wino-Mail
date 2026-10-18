# Android development

This is the guide for the Android application in `src/Wino.Mail.Android` and its solution `WinoMail.Android.slnx`.
One APK serves phones, tablets, and foldables. Layouts follow the window size class, not the device type or orientation.
The app uses the shared .NET services, ViewModels, translations, and editor engine through explicit adapters over Android Views with XML layouts, AndroidX, and Material Components. It does not use Compose, MAUI, or Uno.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md).
Presentation decisions are in [Android design decisions](../android-design-decisions.md). The background study is the [Android port investigation](../android-port-investigation.md); where they differ, the design decisions and this guide win.

## Requirements

Android builds run on a Mac or on Windows.

- The .NET SDK selected by `global.json` (10.0.401), with the `android` workload: `dotnet workload install android`.
- A JDK: Microsoft OpenJDK 17 (21 is also supported). Set `JAVA_HOME` to it.
- The Android SDK with platform 36, build tools, platform tools, the emulator, and system images. Set `ANDROID_HOME` to it, and put `platform-tools`, `emulator`, and `cmdline-tools/latest/bin` on `PATH`.

**Workload set pitfall.** Installing or updating a workload can move the machine's workload set. On 14 Oct 2026 `dotnet workload install android` moved it to 10.0.402, which replaced the iOS Mono AOT packs and broke the iOS Simulator build (`mono-aot-cross` missing). Pin the set back after any workload install, then check every Apple and Android build:

```sh
dotnet workload update --version 10.0.401.1
dotnet workload --info
```

Install the SDK and JDK-dependent parts once through the build, then add the emulator and images with `sdkmanager`:

```sh
dotnet build src/Wino.Mail.Android/Wino.Mail.Android.csproj -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory="$HOME/Library/Android/sdk" -p:JavaSdkDirectory="$HOME/Library/Java/jdk-17" \
  -p:AcceptAndroidSDKLicenses=True -p:WinoTargetPlatform=Android
sdkmanager "emulator" "system-images;android-36;google_apis_playstore;arm64-v8a" \
  "system-images;android-36;google_apis_playstore_ps16k;arm64-v8a" "system-images;android-23;google_apis;arm64-v8a"
```

The 16 KB page-size check needs `llvm-readelf` from NDK r28c: `sdkmanager "ndk;28.2.13676358"` ([Android release packages](../releases-android.md#16-kb-page-size-check)). Builds do not need the NDK.

Last verified combination on the Mac (2026-10-14): .NET SDK 10.0.401, workload set 10.0.401.1, `android` workload 36.1.69, Microsoft OpenJDK 17.0.14 (`~/Library/Java/jdk-17`), Android SDK `~/Library/Android/sdk` with platform `android-36` and build tools 36.0.0.

### Emulators

| AVD | Image | Use |
| --- | --- | --- |
| `Wino_Phone_API36` | `android-36` `google_apis_playstore` arm64, Pixel 8 | Compact and medium (landscape) layouts, Play services sign-in |
| `Wino_Tablet_API36` | `android-36` `google_apis_playstore` arm64, Pixel Tablet | Medium and expanded layouts |
| `Wino_Phone_API36_16KB` | `android-36` `google_apis_playstore_ps16k` arm64, Pixel 8 | 16 KB page-size launch check |
| `Wino_Phone_API23` | `android-23` `google_apis` arm64, Pixel | Minimum OS launch check |

Create one with `avdmanager create avd -n Wino_Phone_API36 -k "system-images;android-36;google_apis_playstore;arm64-v8a" -d pixel_8` and start it with `emulator -avd Wino_Phone_API36`. When several agents share one machine, give each task its own AVD and address it with `adb -s emulator-NNNN`, so installs do not replace each other's builds.

Memory: each running emulator and each Android build uses several gigabytes. On a 16 GB Mac run at most one or two emulators at a time, run Android, Mac, and iOS builds one after another rather than in parallel, and close emulators when a check ends.

### Windows hosts

Install the same SDK, workload, and JDK. Use `x86_64` system images instead of `arm64-v8a`, enable Hyper-V or the Windows Hypervisor Platform (WHPX) for emulator acceleration, and pass `-r android-x64` to Debug builds (the head defaults to `android-arm64`). Each machine signs Debug builds with its own debug keystore (`%LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore`), so each needs its own Entra and Google registration before Outlook or Gmail sign-in works there; see [Sign-in registrations](#sign-in-registrations).

## Platform selection

Android builds pass `-p:WinoTargetPlatform=Android` on every command. The host never selects it: a Mac host resolves `MacOS` and a Windows host `Windows`. The head and the Android libraries also set it in their project files, but portable references such as `Wino.Core.ML` see only the global property, so a command without it builds those for the host platform.
`Directory.Build.targets` imports [build/Wino.Android.targets](../../build/Wino.Android.targets) when `WinoTargetPlatform` is `Android` or the target framework contains `-android`. That file:

- sets `IsAotCompatible=false` and `PublishAot=false` for `-android` targets. The app runs on Mono: JIT in Debug, full AOT without LLVM and full trimming (`TrimMode=full`) for Release (LLVM miscompiles async file reads, [dotnet/runtime#104167](https://github.com/dotnet/runtime/issues/104167); keep it off until a fixed runtime ships) apps (the condition tests `AndroidApplication`, because the SDK turns an app's `OutputType` `Exe` into `Library`). Native AOT for Android is experimental in .NET 10 and stays off. `-android` projects are exempt from the shared Release gate; portable libraries keep their Native AOT and trimming gates.
- defaults `SupportedOSPlatformVersion` to 23 (Android 6.0). The head targets SDK 36.

With `WinoTargetPlatform=Android`, `Wino.Core.ML` defines `WINO_ML_UNSUPPORTED` and leaves out ONNX Runtime and the Magika model: Android has no content-type classification, and attachments open through the system.

Runtime identifiers: Debug builds of the head use `android-arm64` (the Apple silicon emulator ABI); pass `-r android-x64` on Windows and Intel hosts. Release is `android-arm64` only. There are no 32-bit builds.

## Projects

| Project | Role |
| --- | --- |
| `src/Wino.Mail.Android` | App head (`com.winomail.android`): `MainApplication`, the single `MainActivity`, `AndroidComposition`, fragments with XML layouts, navigation and dialog services, manifest, assets |
| `src/Wino.Platform.Android` | Platform services: paths, Keystore protector, secure item store, dispatcher, launcher, clipboard, authentication hosts; no presentation references |
| `controls/Wino.Presentation.Android` | Units, insets, WinoIcons typeface and glyph drawables, colours, zones, wallpaper and bar backdrops, view bindings |
| `controls/Wino.Mail.Controls.Android` | Mail and group rows, list adapter, swipes, shell rows, settings cards and expander, avatars, pills, chips, info bar, calendar week strip, month grid and surface, contact and task rows |
| `controls/Wino.Editor.Android` | `WebView` reader and editor sessions over `Wino.Editor.Core`: assets through `WebViewAssetLoader`, the `winoMail` `WebMessageListener` bridge with a Java `WinoJavaScriptBridge` fallback, remote-content policy in `shouldInterceptRequest` |

`WinoMail.Android.slnx` has two folders: `/Android/` (the projects above) and `/Shared/`.

**Views named only in XML.** Release builds trim with `TrimMode=full`, which removes a view that only a layout names. Every new `[Register]`ed view in `controls/Wino.Presentation.Android` or `controls/Wino.Mail.Controls.Android` that is used only from XML must be listed in that library's `ILLink.Descriptors.xml`; otherwise Debug works and the Release app fails with `ClassNotFoundException` while inflating.

## Build and run

```sh
dotnet build src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Debug -p:WinoTargetPlatform=Android
dotnet build WinoMail.Android.slnx -c Debug -p:WinoTargetPlatform=Android
dotnet build src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Debug -p:WinoTargetPlatform=Android -t:Install
dotnet build src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Debug -p:WinoTargetPlatform=Android -t:Run
```

`-t:Install` deploys to the running emulator; `-t:Run` also launches. With several devices attached, add `-p:AdbTarget="-s emulator-5554"`.
Do not install a Debug APK with `adb install`: fast deployment keeps the assemblies outside the APK, so the app crashes at start.

The launcher activity has a generated Java name. Resolve it, then start it:

```sh
activity=$(adb shell cmd package resolve-activity --brief com.winomail.android | tail -n 1)
adb shell am start -n "$activity"
adb logcat --pid="$(adb shell pidof com.winomail.android)"
adb exec-out screencap -p > phone.png
```

| Check | Command |
| --- | --- |
| Crashes and runtime errors | `adb logcat -s AndroidRuntime DOTNET monodroid` |
| Dark mode | `adb shell cmd uimode night yes` (`no` for light); the activity recreates |
| Rotation | `adb shell settings put system accelerometer_rotation 0`, then `adb shell settings put system user_rotation 1` (landscape) or `0` (portrait) |
| Debug controls gallery | `adb shell am start -n com.winomail.android/com.winomail.android.ControlsGallery` (add `--ez dark true` for dark) |
| Stop the app | `adb shell am force-stop com.winomail.android` |
| Font scale | `adb shell settings put system font_scale 2.0` (`1.0` to reset) |
| Unlabeled controls | `adb shell uiautomator dump /sdcard/u.xml`, then look for `clickable="true"` nodes without `text` or `content-desc` (a WinoIcons glyph character is not a label) |
| Phone size on the tablet | `adb shell wm size 1080x2400` (`wm size reset` afterwards) |

The controls gallery (`Views/Debug/ControlsGalleryActivity.cs`) compiles in Debug only and shows every control from the two control libraries in light or dark.

### Debug-only commands

These activities exist only in Debug builds and are exported so `adb` can start them.

| Activity | Use |
| --- | --- |
| `com.winomail.android.DebugCommand` | `adb shell am start -n com.winomail.android/com.winomail.android.DebugCommand --es command <name>`. Demo data: `seedmail` (a demo account with mail; its sync fails fast on a closed port), `seedrecipients`, `seedcalendar`, `seedcontacts`, `seedtasks`. Background checks are listed under [Background and notifications](#background-and-notifications). |
| `com.winomail.android.SettingsDebug` | `--es command page --es arg <WinoPage>` opens a Settings page; other commands are `settings`, `accountpage`, `dialog`, `theme`, `mode`, `accent`, and `close` |
| `com.winomail.android.ControlsGallery` | The controls gallery above |

Application data lives in `NoBackupFilesDir/data` (database, `preferences.json`, `Logs/wino.log`), secure items in `NoBackupFilesDir/secure`, and the wrapped protection key in `NoBackupFilesDir/protection`. Auto Backup is off (`android:allowBackup="false"`). The protection key is wrapped by the non-exportable Android Keystore key `wino-protection-v1`; clearing app data or uninstalling removes credentials.

## Sign-in registrations

Outlook (MSAL) and Gmail (Play services) identify the app by package name and signing certificate, so every signing key needs its own registration:

| Service | Registration | Value per signing key |
| --- | --- | --- |
| Microsoft Entra | Add platform, Android, package `com.winomail.android` | Signature hash (Base64 of the certificate SHA-1). The app builds the redirect `msauth://com.winomail.android/<hash>` at runtime with the raw hash (MSAL's broker check requires it; MSAL URL-encodes it for Entra, and the portal shows the encoded form). |
| Google Cloud | Android OAuth client for package `com.winomail.android` | Certificate SHA-1 |

Compute both from the debug keystore (Mac path below; password `android`, alias `androiddebugkey`):

```sh
ks="$HOME/Library/Application Support/Xamarin/Mono for Android/debug.keystore"
keytool -list -v -keystore "$ks" -alias androiddebugkey -storepass android | grep SHA1
keytool -exportcert -keystore "$ks" -alias androiddebugkey -storepass android | openssl sha1 -binary | openssl base64
```

For the release key, use the upload keystore and alias instead; with Play App Signing, also register the app signing certificate SHA-1 from the Play Console.
Gmail uses the Play services `AuthorizationClient` when Play services is available, and falls back to the shared `GmailAuthenticator` loopback flow (a Custom Tab with the existing desktop client) on devices without it or for accounts with a stored refresh token. The MSAL broker (Microsoft Authenticator) is off.
Until the registrations exist, Outlook and Gmail sign-in cannot be verified; IMAP can.

Wino Account Google sign-in uses Credential Manager (`AndroidGoogleSignInProvider`, `Xamarin.AndroidX.Credentials` + `GoogleId`) with the web client as `serverClientId`; it needs the Android OAuth client for the signing certificate (the Google Cloud table above) and a Google account on the device or emulator (a Play-services image). Wino Account Sign in with Apple needs no registration: Android runs the API browser flow (`api/v1/auth/apple/authorize` in a Custom Tab), and `WinoAccountAuthRedirectActivity` receives the API's redirect through its `wino://auth-complete` intent filter. The scheme and host come from `WinoAccountExternalAuth` in `Wino.Core.Domain` and must equal the API's `Secrets:ExternalAuth:AppCallbackUri`. To exercise the redirect without Apple, open the sign-in dialog and run `adb shell am start -a android.intent.action.VIEW -d "wino://auth-complete?errorCode=EXTERNAL_LOGIN_INVALID"`: the dialog comes back with the translated error.

## Background and notifications

The shared runtime runs while an activity is started; 10 minutes after the last one stops, queued changes are sent, the runtime stops and the process ends (`AndroidForegroundLifecycle`). There is no periodic background synchronization.

- **Queued changes:** every queued change request (`ISynchronizationManager.RequestsQueued`) and every move to the background enqueue the unique, expedited WorkManager work `wino-flush-queued-changes` with a network constraint; `PendingChangesWorker` runs `IApplicationRuntime.FlushQueuedRequestsAsync`. The queues live in memory, so the work only helps while the process lives. Inspect it with `adb shell dumpsys jobscheduler | grep -A3 winomail`.
- **Reminders:** the in-process poller (`ICalendarReminderServer`) while the runtime runs; otherwise an AlarmManager wake-up for the next reminder (`AndroidReminderScheduler`), inexact unless the user allows exact alarms (`adb shell appops set com.winomail.android SCHEDULE_EXACT_ALARM allow` to test). It is set again after a reboot or an update.
- **Notifications:** `AndroidNotificationBuilder` over the shared policy and `NotificationContentRules`: one channel per account (group "Mail accounts"), calendar, account alerts and a silent background channel; mail grouped per account; the two configured mail actions (Settings, Notifications) through `NotificationActionReceiver`; taps open the activity and go to the shared `INotificationResponseService`. The launcher badge is the notification count.
- **Permission:** `POST_NOTIFICATIONS` (Android 13+). The shell asks once when it first appears (`AndroidNotificationPermission.RequestOnceAsync`); onboarding does not ask.
- **FCM (WinoRelay):** Firebase starts only in builds with `src/Wino.Mail.Android/google-services.json` (from the Firebase console, package `com.winomail.android`). Without it there is no token and `RemoteNotifications` is off.

Debug commands (`adb shell am start -n com.winomail.android/com.winomail.android.DebugCommand --es command <name>`): `testnotification` (`--es kind mail|calendar|attention|people|task`), `seedreminder` (`--ei minutes N`: a reminder due in about a minute), `graceperiod` (`--ei seconds N`, `0` restores 10 minutes), `backgroundstate` (runtime, queued changes, alarm; written to the log), `relaytest` (the relay test push to this device; needs a registered relay device).

## VS Code

Open [WinoMail.Android.code-workspace](../../WinoMail.Android.code-workspace). It selects `WinoMail.Android.slnx`. **Ctrl+Shift+B** runs **Build Wino Mail (Android, Debug)**; **Install and run Wino Mail (Android emulator)** deploys and launches on the running emulator; **Build Wino Mail Android solution** builds `WinoMail.Android.slnx`. The workspace has no debugger launch configuration yet.

## Verification

| Change | Required evidence |
| --- | --- |
| Android views, fragments, layouts, controls, navigation, or activation | Android Debug build, plus manual checks on the phone and tablet emulators |
| Android platform services | Android Debug build, plus manual checks of the affected service; sign-in needs the registrations above |
| Release inputs (manifest, signing, trimming, AOT, packaging) | Release Mono AOT build and the 16 KB page-size check ([Android release packages](../releases-android.md)) |
| Shared code used by the Android app | The Android Debug build, plus the Windows, Mac, and iOS checks in [AGENTS.md](../../AGENTS.md) |

Manual checks cover the three width classes (phone portrait, phone landscape, tablet), light and dark, rotation and dark-mode recreation without losing the page, back navigation, font scale, and TalkBack. A build does not prove UI behavior. The API 23 emulator checks that the app still starts on the minimum OS.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| iOS build fails at `AOTCompile` after an Android workload install | The workload set moved. Run `dotnet workload update --version 10.0.401.1`. |
| Release restore fails with NU1902/NU1903 for `System.Security.Cryptography.Pkcs` 10.0.0 | Known issue: a pre-existing advisory in the shared graph, an error in Release. Until the package is updated, builds that must proceed pass `-p:NuGetAudit=false`; report it as a known issue, not a fix. Debug shows it as warnings. |
| App closes at start after `adb install` | Fast deployment. Use `-t:Install` or `-t:Run`. |
| Manifest merge fails after a package update | An AndroidX or Play services version needs minSdk 24 or later. Keep the version that supports 23: `androidx.webkit` 1.15, `play-services-auth` 21.x. |
| Sentry build error about assemblies | Keep `AndroidEnableAssemblyCompression` on in the head. |
| Release app: `ClassNotFoundException` while inflating a `com.winomail.*` view | Full trimming removed a view that only XML names. Add it to the library's `ILLink.Descriptors.xml`. |
| Release app: `TypeLoadException` in `Mono.Android.Runtime` at start | Stale incremental Release output. Delete `src/Wino.Mail.Android/obj/Release` and `bin/Release` and publish again. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | The installed app was signed with another key (another machine or Release). Uninstall it with `adb uninstall com.winomail.android`; app data is lost. |

## Release

See [Android release packages](../releases-android.md). There is no Android release script yet. Build Release from a clean output (delete `src/Wino.Mail.Android/obj/Release` and `bin/Release` first): an incremental Release build produced an APK that crashed at start.
