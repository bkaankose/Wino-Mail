# AGENTS.md

This file provides guidance for agents working in `controls/`, Mail-Cal's reusable controls workspace.

## Scope

The directory has two groups. Read the rules for each group you change:

| Group | Projects | Platform guide |
| --- | --- | --- |
| Shared contracts and editor engine | `MailCal.Mail.Controls.Core`, `MailCal.Editor.Core` | Platform-neutral; verify through [Windows](../docs/harness/development-windows.md) |
| WinUI 3 and WebView2 | `MailCal.Mail.Controls.WinUI`, `MailCal.Editor.WinUI`, `MailCal.Mail.Controls.Playground.WinUI` | [Windows](../docs/harness/development-windows.md) |

The projects:

- `MailCal.Mail.Controls.Core` contains platform-neutral models, interfaces, projections, and collection logic. Keep this project free of WinUI dependencies so its `net10.0` target remains usable outside Windows UI code.
- `MailCal.Mail.Controls.WinUI` contains reusable WinUI 3 controls. Public controls belong in a matching feature folder and namespace, such as `MailListView/MailCalMailListView.cs` in `MailCal.Mail.Controls.MailListView`.
- `MailCal.Editor.Core` owns portable sessions, serialization, operation queues, document policies, and the sole embedded HTML, CSS, and JavaScript bundle.
- `MailCal.Editor.WinUI` adapts these contracts to WebView2.
- `MailCal.Mail.Controls.Playground.WinUI` is the development and verification app. It must demonstrate every public UI control.

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
dotnet build controls\MailCal.Mail.Controls.Core\MailCal.Mail.Controls.Core.csproj -c Debug -p:Platform=x64

# WinUI control library
dotnet build controls\MailCal.Mail.Controls.WinUI\MailCal.Mail.Controls.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# WebView2 editor library
dotnet build controls\MailCal.Editor.WinUI\MailCal.Editor.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# Playground app and all referenced control projects
dotnet build controls\MailCal.Mail.Controls.Playground.WinUI\MailCal.Mail.Controls.Playground.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```

After the first successful restore, add `--no-restore` unless package or project references changed. Prefer the narrow affected-project build; build the playground when UI, resources, templates, or public control integration changes.

If the WinUI compiler reports only `XamlCompiler.exe exited with code 1`, rerun the failing build with diagnostic file logging and inspect the first `WMC` or binding error instead of guessing.

## Adding or Changing WinUI Controls

- Name reusable controls `Mail-Cal{ControlName}`.
- In `MailCal.Mail.Controls.WinUI`, place each control and its support types in a matching `{ControlName}/` folder and `MailCal.Mail.Controls.{ControlName}` namespace.
- Define visual structure, templates, flyouts, and control declarations in XAML. Keep code-behind limited to event handling and view glue.
- Put default styles and templates for custom controls in `MailCal.Mail.Controls.WinUI/Themes/Generic.xaml`.
- Prefer `[GeneratedDependencyProperty]` for new WinUI dependency properties. Do not introduce new manual `DependencyProperty.Register(...)` declarations without a concrete compatibility reason.
- Do not create `IValueConverter` implementations. Use direct WinUI conversion or an existing helper.
- Give interactive elements stable, meaningful automation names or IDs. Preserve keyboard, pointer, touch, screen-reader, Light/Dark, and High Contrast behavior.
- When using `x:Load`, always give the element an `x:Name`.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not in constructors.
- Keep public APIs small and host-independent. Avoid references from a reusable control back to `MailCal.Mail.WinUI`.
- Icons use the library's `AccountIcon.MailCalFontIcon` / `MailCalFontIconSource`. They are backed by the monochrome MailCalIcons font that `icons/tools/build_fonts.py` writes into `MailCal.Mail.Controls.WinUI/Assets`.
  - In C#, use the generated `MailCalIconCodes` constants, for example `MailCalIconCodes.Delete`.
  - In templates, use the codepoint from `icons/manifest.json`, for example `Glyph="&#xEEA6;"`.
  - Never use Segoe glyphs, `SymbolIcon` or `PathIcon`. `MailCal.Editor.WinUI` references this library for the same icon.

When a public control is added or its important states change, update `MailCal.Mail.Controls.Playground.WinUI` in the same change:

1. Add or update a focused page under `Pages/`.
2. Keep sample models and view models under `Models/` and `ViewModels/`.
3. Add navigation in both `MainPage.xaml` and `MainPage.xaml.cs` when introducing a page.
4. Expose the states needed to verify normal, empty, loading, disabled, error, and theme behavior when applicable.

## Core Library Rules

- Keep the `net10.0` target platform-neutral. Windows-only code must be isolated to the Windows target and guarded consistently with the existing project setup.
- Prefer interfaces and immutable projection/state models at the control boundary.
- Keep collection projection, selection, grouping, and thread-expansion rules deterministic and independent of XAML controls.
- Changes to public core contracts must be checked against both `MailCal.Mail.Controls.WinUI` and consumers under `src/`.

## Editor Rules

- `MailCal.Editor.Core` is the single source for reader/editor HTML, CSS, and JavaScript. Do not create a second asset bundle in the playground or main app.
- Keep web assets embedded through the existing `Editor/**/*` project rule.
- Preserve the document-ready bridge handshake before invoking rendering or editing commands.
- Use the source-generated `EditorJsonContext` for every .NET/JavaScript payload. Reflection-based JSON serialization is disabled for trimming and Native AOT compatibility.
- Keep public bridge models explicitly named and serialization-safe. When changing a command or payload, update its C# contract, JavaScript handler, and playground scenario together.
- Dispose WebView2 or WKWebView and bridge resources, detach handlers, and avoid retaining pages or editor controls after navigation.
- Treat HTML as untrusted input. Preserve navigation interception and do not weaken sanitization or script boundaries.

## Playground Rules

- The playground is sample and verification code, not a home for reusable behavior.
- Use realistic but deterministic local sample data. Do not require live accounts, network access, secrets, or Mail-Cal's database.
- Keep pages focused on one control family and make behavior easy to inspect manually.
- Do not duplicate a reusable style, model, or behavior in the playground merely to make a demo work; move the reusable part into the correct library.

## Verification

The playground remains a sample app. The local Docker lab is the application testing environment.
Agent-driven UI automation is retired.

Match verification to the change:

- Core-only logic: build `MailCal.Mail.Controls.Core` and its consumers.
- WinUI control or template: build `MailCal.Mail.Controls.WinUI` and the playground, then request manual verification of the affected control in the app with the local lab.
- Editor C#, XAML, or web assets: build `MailCal.Editor.WinUI` and the playground, then request manual lab checks for initialization, editing/rendering, theme changes, navigation, and disposal.
- Shared Core or editor engine change: verify the WinUI consumers above. Keep the code free of WinUI references so a future Linux head can reuse it.
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
