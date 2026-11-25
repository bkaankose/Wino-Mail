# Wino.Mail.Android

The Android app: Android Views with XML layouts, AndroidX and Material Components, written in C# over the shared services and ViewModels. Mono runs JIT in Debug and full AOT with full trimming in Release; Native AOT stays off. This file is binding for `src/Wino.Mail.Android` and `src/Wino.Platform.Android`.

## Read first

| When you | Read |
| --- | --- |
| Build, deploy, use an emulator or check the app | [Android development](../../docs/harness/development-android.md) |
| Add a screen or a visual pattern | [Android design decisions](../../docs/android-design-decisions.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |
| Change the manifest, signing, trimming or AOT | [Android release guide](../../docs/releases-android.md) |

## Hard stops

- Never build, deploy or debug this app on a Windows machine. Use a Linux or Mac host, or report the Android check as pending.
- Pass `-p:WinoTargetPlatform=Android` on every command. The host never selects Android.
- Deploy with `-t:Install` or `-t:Run`. Never `adb install` a Debug APK.
- After any workload install, pin the workload set back as the Android guide states.
- Keep minSdk 24 and target SDK 36. Release is `android-arm64` only.
- Google matches the package `com.winomail.android` and the signing certificate; Android client ids are never named in code. Each signing key needs its own registrations.
- The browser sign-in callback scheme must equal the API's `AppCallbackUri`. It is read from `WinoAccountExternalAuth` in `Wino.Core.Domain`.
- There is no ONNX on Android (`WINO_ML_UNSUPPORTED`).
- Mail is read, printed and composed with the native mail renderer, never a `WebView`. It comes as the `WinoMail.Renderer.Android` package; `WINO_RENDERER_ROOT` (or `-p:WinoRendererRoot`) builds against a renderer checkout instead. `WebView` remains only in `EmbeddedBrowserSignInDialog`, the embedded browser auth broker and the intelligence policy sheet, which load web pages.

## Mail renderer

- `build/Wino.Renderer.Android.props` references the engine, the Android backend (`Wino.Renderer.Platform.Android`) and the Android view (`Wino.Renderer.Android`). `AndroidComposition` registers `AndroidMailEngineBackendFactory` and calls `AddMailRendering()`: one `MailEngine` per process (`MailEngineHost`).
- The reader page creates the renderer's `MailView` in code (auto height inside the page's scroll view) and shows the message through the shared `EngineMailMessageReader`; the composer page hosts a self-scrolling `MailView` behind the shared `EngineMailEditorSession`. `AndroidMailPrintPresenter` prints through the system print dialog with the renderer's `MailPrintAdapter`.
- Links, remote content, tracker stripping, dark mode and the text scale (the system font size) come from the engine and `EngineReaderPresentation`. An engine fault is fixed in the renderer repository, not hidden here.

## How a view consumes a ViewModel

A page is a `WinoFragment<TViewModel>` ([Infrastructure/WinoFragment.cs](Infrastructure/WinoFragment.cs)) with an XML layout:

- The ViewModel lives on a `PageEntry`, not on the fragment, so it survives view recreation. `AndroidPageRegistry` resolves it and `AndroidNavigationService` creates the entry. Register the route in [Infrastructure/AndroidPageRegistry.cs](Infrastructure/AndroidPageRegistry.cs).
- The entry assigns `ViewModel.Dispatcher` and calls `OnNavigatedTo`, `OnPageLoaded` and `OnNavigatedFrom`.
- Override `LayoutResource` and `OnBind(view, viewModel, dispatcher)`. Bindings made there are rebuilt with each view and disposed in `OnDestroyView`. `ViewBinder` in `controls/Wino.Presentation.Android/Bindings` has the helpers (`Text`, `Visible`, `Enabled`, `Command`, `Click`, `Collection`, `TwoWayText`).

```csharp
Button(view, Resource.Id.welcome_add_account, Translator.WelcomeWindow_GetStartedButton,
    viewModel.GetStartedCommand, dispatcher);

if (view.FindViewById(Resource.Id.welcome_import_progress) is { } progress)
{
    Bind(viewModel, dispatcher, nameof(WelcomePageV2ViewModel.IsImportInProgress), vm => vm.IsImportInProgress,
        running => progress.Visibility = running ? ViewStates.Visible : ViewStates.Gone);
}
```

A page reads state and invokes commands on its ViewModel. When a page needs data the ViewModel does not expose, add it to the shared ViewModel; about 70 pages still resolve a service directly, and new code does not add to them.

## Always

- Put a business rule in shared code. If a screen needs a rule that only one head has, move it to shared code first; never write a third copy.
- Let the window size class pick the layout, never the device type or the orientation.
- List every view that only an XML layout names (`[Register]`) in the project's `ILLink.Descriptors.xml`, or Release trimming removes it.
- Register every new service in [AndroidComposition.cs](AndroidComposition.cs); platform services go to `Wino.Platform.Android`.

## Check your change

- **On a Linux or Mac host:** the Android Debug build, then manual checks on the phone and tablet emulators. Run at most two emulators and run builds one after another. Commands are in the Android guide.
- **On a Windows machine:** build the shared projects you touched, follow the pattern above for the view code, and report "Android Debug build and manual checks pending" with the list of changed files.

A build does not prove UI behavior. The NU1902 and NU1903 audit failure is a known issue; leave it.

## Performance

- Keep `AndroidEnableAssemblyCompression` on.
- Build Release from a clean output.
