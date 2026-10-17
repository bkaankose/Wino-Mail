# AGENTS.md

This file provides guidance for agents working in `controls/`, Wino Mail's reusable controls workspace.

## Scope

The directory has five groups. Read the rules for each group you change:

| Group | Projects | Platform guide |
| --- | --- | --- |
| Shared contracts and editor engine | `Wino.Mail.Controls.Core`, `Wino.Editor.Core`, `Wino.Presentation.Core` | All: [Windows](../docs/harness/development-windows.md), [macOS](../docs/harness/development-macos.md), [iOS](../docs/harness/development-ios.md), and [Android](../docs/harness/development-android.md) |
| WinUI 3 and WebView2 | `Wino.Mail.Controls.WinUI`, `Wino.Editor.WinUI`, `Wino.Mail.Controls.Playground.WinUI` | [Windows](../docs/harness/development-windows.md) |
| AppKit and WKWebView | `Wino.Presentation.AppKit`, `Wino.Mail.Controls.AppKit`, `Wino.Editor.WebKit` (macOS and iOS) | [macOS](../docs/harness/development-macos.md); `Wino.Editor.WebKit` also [iOS](../docs/harness/development-ios.md) |
| UIKit (iOS and iPadOS) | `Wino.Presentation.UIKit`, `Wino.Mail.Controls.UIKit` | [iOS](../docs/harness/development-ios.md) |
| Android Views and WebView | `Wino.Presentation.Android`, `Wino.Mail.Controls.Android`, `Wino.Editor.Android` | [Android](../docs/harness/development-android.md) |

The projects:

- `Wino.Mail.Controls.Core` contains platform-neutral models, interfaces, projections, and collection logic. Keep this project free of WinUI dependencies so its `net10.0` target remains usable outside Windows UI code.
- `Wino.Mail.Controls.WinUI` contains reusable WinUI 3 controls. Public controls belong in a matching feature folder and namespace, such as `MailListView/WinoMailListView.cs` in `Wino.Mail.Controls.MailListView`.
- `Wino.Editor.Core` owns portable sessions, serialization, operation queues, document policies, and the sole embedded HTML, CSS, and JavaScript bundle.
- `Wino.Presentation.Core` (`net10.0`) holds platform-neutral presentation helpers for the native Apple and Android heads: typed bindings (`Wino.Presentation.Bindings`, including `AnyPropertyBinding`), design tokens (`WinoTokens`, `WinoRgba`), `WinoThemeSurface`, and control rules that need Domain types (`Controls/WinoControlRules.cs`: mode segments, provider glyphs, calendar tile appearance, intelligence header text, contact row rules). It references `Wino.Core.Domain` and `Wino.Mail.Controls.Core` but no UI framework. WinUI does not reference it.
- `Wino.Editor.WinUI` adapts these contracts to WebView2.
- `Wino.Presentation.AppKit` and `Wino.Mail.Controls.AppKit` contain native macOS layout helpers and reusable controls. Verify them in the production Mac head; do not create an AppKit playground.
- `Wino.Editor.WebKit` (`net10.0-macos;net10.0-ios`, formerly `Wino.Editor.AppKit`) contains the WKWebView reader and editor sessions (`WebKitHtmlMailReaderSession`, `WebKitHtmlMailEditorSession`) and `AppleMailDocumentBuilder` for both Apple heads. It uses no AppKit or UIKit types apart from one guarded focus call; each head creates, configures, and hosts the WKWebView, and owns the keyboard accessory and printing. It narrows to one target framework when an `osx-*` or `ios-*` RID is passed.
- `Wino.Presentation.UIKit` and `Wino.Mail.Controls.UIKit` (`net10.0-ios`, iOS 17.0) are the UIKit twins of the AppKit libraries, with the same member names and feature folders (`MailList`, `Shell`, `Common`, `Settings`, `Calendar`, `Extras`, `ToDo`, `Contacts`, `Intelligence`). `Wino.Presentation.UIKit` provides `WinoLayout`, `WinoIcons`/`WinoIconView` (registers the bundled WinoIcons fonts; use `WinoIcons.TemplateImage` for bars, tabs, and menus), `WinoStyle` with Dynamic Type fonts, `WinoZoneView`, `ThemeBackdropView`, and `WinoBarBackdropView`. Pages own swipe actions and context menus; cells only render. Logic both twins need belongs in `Wino.Mail.Controls.Core` (pure math) or `Wino.Presentation.Core/Controls` (rules that use Domain types). Verify them in the production iOS head; there is no UIKit playground.
- `Wino.Presentation.Android` and `Wino.Mail.Controls.Android` (`net10.0-android`, minSdk 23) are the Android twins of the UIKit libraries, built from code and XML layouts over AndroidX and Material Components. `Wino.Presentation.Android` provides units, insets, the WinoIcons typeface, colours and tokens (`WinoTokens`, resources under `Resources/values`), zones, and wallpaper and bar backdrops; `Wino.Mail.Controls.Android` provides mail and shell rows, the list adapter, settings cards, calendar surfaces, and contact and task rows. Views that layouts name by Java name (`[Register]`) and nothing references in code must be listed in the library's `ILLink.Descriptors.xml`, or Release trimming removes them. Verify them in the production Android head; the Debug-only `ControlsGallery` activity shows every control in light and dark.
- `Wino.Editor.Android` (`net10.0-android`) contains the Android `WebView` reader and editor sessions over `Wino.Editor.Core`, with assets served by `WebViewAssetLoader` and the `winoMail` message bridge (a Java `WinoJavaScriptBridge` fallback where `WebMessageListener` is missing).
- `Wino.Mail.Controls.Playground.WinUI` is the development and verification app. It must demonstrate every public UI control.

Do not move application-specific mail behavior into these projects. Keep reusable contracts and collection behavior in Core, reusable presentation behavior in the control libraries, and sample-only data or wiring in the playground.

## Repository Navigation

The repository root contains `.codegraph/`. Use CodeGraph before text search or broad file reads when locating symbols, callers, or control relationships:

```powershell
codegraph explore "describe the symbol or control to inspect"
```

Start with the named control, feature folder, or playground page. Avoid mapping the whole repository for a focused change.

## Build Commands

Run commands from the repository root. On Windows, use x64 for routine verification.

```powershell
# Platform-neutral logic (build both target frameworks)
dotnet build controls\Wino.Mail.Controls.Core\Wino.Mail.Controls.Core.csproj -c Debug -p:Platform=x64

# WinUI control library
dotnet build controls\Wino.Mail.Controls.WinUI\Wino.Mail.Controls.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# WebView2 editor library
dotnet build controls\Wino.Editor.WinUI\Wino.Editor.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# Playground app and all referenced control projects
dotnet build controls\Wino.Mail.Controls.Playground.WinUI\Wino.Mail.Controls.Playground.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```

AppKit libraries build on a Mac through the production head (see the [macOS guide](../docs/harness/development-macos.md)):

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

UIKit libraries build on a Mac through the iOS head (see the [iOS guide](../docs/harness/development-ios.md)):

```sh
dotnet build src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Debug -r iossimulator-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true
```

Android libraries build on a Mac or Windows through the Android head (see the [Android guide](../docs/harness/development-android.md)):

```sh
dotnet build src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Debug -p:WinoTargetPlatform=Android
```

After the first successful restore, add `--no-restore` unless package or project references changed. Prefer the narrow affected-project build; build the playground when UI, resources, templates, or public control integration changes.

If the WinUI compiler reports only `XamlCompiler.exe exited with code 1`, rerun the failing build with diagnostic file logging and inspect the first `WMC` or binding error instead of guessing.

## Adding or Changing WinUI Controls

- Name reusable controls `Wino{ControlName}`.
- In `Wino.Mail.Controls.WinUI`, place each control and its support types in a matching `{ControlName}/` folder and `Wino.Mail.Controls.{ControlName}` namespace.
- Define visual structure, templates, flyouts, and control declarations in XAML. Keep code-behind limited to event handling and view glue.
- Put default styles and templates for custom controls in `Wino.Mail.Controls.WinUI/Themes/Generic.xaml`.
- Prefer `[GeneratedDependencyProperty]` for new WinUI dependency properties. Do not introduce new manual `DependencyProperty.Register(...)` declarations without a concrete compatibility reason.
- Do not create `IValueConverter` implementations. Use direct WinUI conversion or an existing helper.
- Give interactive elements stable, meaningful automation names or IDs. Preserve keyboard, pointer, touch, screen-reader, Light/Dark, and High Contrast behavior.
- When using `x:Load`, always give the element an `x:Name`.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not in constructors.
- Keep public APIs small and host-independent. Avoid references from a reusable control back to `Wino.Mail.WinUI`.
- Icons use the library's `AccountIcon.WinoFontIcon` / `WinoFontIconSource`. They are backed by the monochrome WinoIcons font that `icons/tools/build_fonts.py` writes into `Wino.Mail.Controls.WinUI/Assets`.
  - In C#, use the generated `WinoIconCodes` constants, for example `WinoIconCodes.Delete`.
  - In templates, use the codepoint from `icons/manifest.json`, for example `Glyph="&#xEEA6;"`.
  - Never use Segoe glyphs, `SymbolIcon` or `PathIcon`. `Wino.Editor.WinUI` references this library for the same icon.

When a public control is added or its important states change, update `Wino.Mail.Controls.Playground.WinUI` in the same change:

1. Add or update a focused page under `Pages/`.
2. Keep sample models and view models under `Models/` and `ViewModels/`.
3. Add navigation in both `MainPage.xaml` and `MainPage.xaml.cs` when introducing a page.
4. Expose the states needed to verify normal, empty, loading, disabled, error, and theme behavior when applicable.

## Core Library Rules

- Keep the `net10.0` target platform-neutral. Windows-only code must be isolated to the Windows target and guarded consistently with the existing project setup.
- Prefer interfaces and immutable projection/state models at the control boundary.
- Keep collection projection, selection, grouping, and thread-expansion rules deterministic and independent of XAML controls.
- Changes to public core contracts must be checked against both `Wino.Mail.Controls.WinUI` and consumers under `src/`.

## Editor Rules

- `Wino.Editor.Core` is the single source for reader/editor HTML, CSS, and JavaScript. Do not create a second asset bundle in the playground or main app.
- Keep web assets embedded through the existing `Editor/**/*` project rule.
- Preserve the document-ready bridge handshake before invoking rendering or editing commands.
- Use the source-generated `EditorJsonContext` for every .NET/JavaScript payload. Reflection-based JSON serialization is disabled for trimming and Native AOT compatibility.
- Keep public bridge models explicitly named and serialization-safe. When changing a command or payload, update its C# contract, JavaScript handler, and playground scenario together.
- Dispose WebView2 or WKWebView and bridge resources, detach handlers, and avoid retaining pages or editor controls after navigation.
- Treat HTML as untrusted input. Preserve navigation interception and do not weaken sanitization or script boundaries.

## Playground Rules

- The playground is sample and verification code, not a home for reusable behavior.
- Use realistic but deterministic local sample data. Do not require live accounts, network access, secrets, or Wino Mail's database.
- Keep pages focused on one control family and make behavior easy to inspect manually.
- Do not duplicate a reusable style, model, or behavior in the playground merely to make a demo work; move the reusable part into the correct library.

## Verification

The playground remains a sample app. The local Docker lab is the application testing environment.
Agent-driven UI automation is retired.

Match verification to the change:

- Core-only logic: build `Wino.Mail.Controls.Core` and its consumers.
- WinUI control or template: build `Wino.Mail.Controls.WinUI` and the playground, then request manual verification of the affected control in the app with the local lab.
- Editor C#, XAML, or web assets: build `Wino.Editor.WinUI` and the playground, then request manual lab checks for initialization, editing/rendering, theme changes, navigation, and disposal.
- AppKit control: build the Mac head for arm64 and x64, then request manual verification in the Mac app. There is no AppKit playground.
- UIKit control: build the iOS head for the Simulator, then request manual verification on iPhone and iPad simulators. There is no UIKit playground.
- WKWebView session (`Wino.Editor.WebKit`): build both Apple heads and request manual checks in both apps.
- Android control or `WebView` session: build the Android head, then request manual verification on the phone and tablet emulators. A new XML-only view also needs a Release build check. There is no Android playground; the Debug `ControlsGallery` activity is a check aid.
- Shared Core or editor engine change: verify the WinUI consumers above and hand off, or run, the Mac, iOS, and Android builds.
- Public API change: search CodeGraph for consumers and build each affected project.

Do not report a UI change as verified from compilation alone. If interactive verification is unavailable, state that clearly in the handoff.

Format changed WinUI XAML before compilation. Run passive verification before handoff:

```powershell
.\scripts\maintenance\windows\format-xaml.ps1 -Changed
.\scripts\maintenance\windows\format-xaml.ps1 -Changed -Check
```

The editor extension is a convenience. The pinned command-line result is the formatting source of truth.

## General Constraints

- Follow the repository-level `AGENTS.md`; this file adds controls-specific guidance.
- Preserve user changes in a dirty worktree and avoid unrelated formatting churn.
- Do not edit generated `obj/` or `bin/` output.
- Avoid new NuGet packages unless the existing platform and libraries cannot solve the problem.
- Keep published cross-repository dependencies as unconditional `PackageReference` items; a sibling checkout must not change the dependency graph.
