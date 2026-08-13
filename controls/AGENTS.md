# AGENTS.md

This file provides guidance for agents working in `controls/`, Wino Mail's reusable controls workspace and WinUI 3 playground.

## Scope

The directory contains four related projects:

- `Wino.Mail.Controls.Core` contains platform-neutral models, interfaces, projections, and collection logic. Keep this project free of WinUI dependencies so its `net10.0` target remains usable outside Windows UI code.
- `Wino.Mail.Controls` contains reusable WinUI 3 controls. Public controls belong in a matching feature folder and namespace, such as `MailListView/WinoMailListView.cs` in `Wino.Mail.Controls.MailListView`.
- `Wino.Editor` contains the reusable WebView2 mail reader/editor and its embedded HTML, CSS, and JavaScript assets.
- `Wino.Mail.Controls.Playground` is the development and verification app. It must demonstrate every public UI control.

Do not move application-specific mail behavior into these projects. Keep reusable contracts and collection behavior in Core, reusable presentation behavior in the control libraries, and sample-only data or wiring in the playground.

## Repository Navigation

The repository root contains `.codegraph/`. Use CodeGraph before text search or broad file reads when locating symbols, callers, or control relationships:

```powershell
codegraph explore "describe the symbol or control to inspect"
```

Start with the named control, feature folder, or playground page. Avoid mapping the whole repository for a focused change.

## Build Commands

Run commands from the repository root (`D:\Wino-Mail`). Use x64 for routine verification.

```powershell
# Platform-neutral logic (build both target frameworks)
dotnet build controls\Wino.Mail.Controls.Core\Wino.Mail.Controls.Core.csproj -c Debug -p:Platform=x64

# WinUI control library
dotnet build controls\Wino.Mail.Controls\Wino.Mail.Controls.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# WebView2 editor library
dotnet build controls\Wino.Editor\Wino.Editor.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# Playground app and all referenced control projects
dotnet build controls\Wino.Mail.Controls.Playground\Wino.Mail.Controls.Playground.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```

After the first successful restore, add `--no-restore` unless package or project references changed. Prefer the narrow affected-project build; build the playground when UI, resources, templates, or public control integration changes.

If the WinUI compiler reports only `XamlCompiler.exe exited with code 1`, rerun the failing build with diagnostic file logging and inspect the first `WMC` or binding error instead of guessing.

## Adding or Changing Controls

- Name reusable controls `Wino{ControlName}`.
- In `Wino.Mail.Controls`, place each control and its support types in a matching `{ControlName}/` folder and `Wino.Mail.Controls.{ControlName}` namespace.
- Define visual structure, templates, flyouts, and control declarations in XAML. Keep code-behind limited to event handling and view glue.
- Put default styles and templates for custom controls in `Wino.Mail.Controls/Themes/Generic.xaml`.
- Prefer `[GeneratedDependencyProperty]` for new WinUI dependency properties. Do not introduce new manual `DependencyProperty.Register(...)` declarations without a concrete compatibility reason.
- Do not create `IValueConverter` implementations. Use direct WinUI conversion or an existing helper.
- Give interactive elements stable, meaningful automation names or IDs. Preserve keyboard, pointer, touch, screen-reader, Light/Dark, and High Contrast behavior.
- When using `x:Load`, always give the element an `x:Name`.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not in constructors.
- Keep public APIs small and host-independent. Avoid references from a reusable control back to `Wino.Mail.WinUI`.

When a public control is added or its important states change, update `Wino.Mail.Controls.Playground` in the same change:

1. Add or update a focused page under `Pages/`.
2. Keep sample models and view models under `Models/` and `ViewModels/`.
3. Add navigation in both `MainPage.xaml` and `MainPage.xaml.cs` when introducing a page.
4. Expose the states needed to verify normal, empty, loading, disabled, error, and theme behavior when applicable.

## Core Library Rules

- Keep the `net10.0` target platform-neutral. Windows-only code must be isolated to the Windows target and guarded consistently with the existing project setup.
- Prefer interfaces and immutable projection/state models at the control boundary.
- Keep collection projection, selection, grouping, and thread-expansion rules deterministic and independent of XAML controls.
- Changes to public core contracts must be checked against both `Wino.Mail.Controls` and consumers under `src/`.

## Editor Rules

- `Wino.Editor` is the single source for reader/editor HTML, CSS, and JavaScript. Do not create a second asset bundle in the playground or main app.
- Keep web assets embedded through the existing `Editor/**/*` project rule.
- Preserve the document-ready bridge handshake before invoking rendering or editing commands.
- Use the source-generated `EditorJsonContext` for every .NET/JavaScript payload. Reflection-based JSON serialization is disabled for trimming and Native AOT compatibility.
- Keep public bridge models explicitly named and serialization-safe. When changing a command or payload, update its C# contract, JavaScript handler, and playground scenario together.
- Dispose WebView2 and bridge resources, detach handlers, and avoid retaining pages or editor controls after navigation.
- Treat HTML as untrusted input. Preserve navigation interception and do not weaken sanitization or script boundaries.

## Playground Rules

- The playground is sample and verification code, not a home for reusable behavior.
- Use realistic but deterministic local sample data. Do not require live accounts, network access, secrets, or Wino Mail's database.
- Keep pages focused on one control family and make behavior easy to inspect manually.
- Do not duplicate a reusable style, model, or behavior in the playground merely to make a demo work; move the reusable part into the correct library.

## Verification

Match verification to the change:

- Core-only logic: build `Wino.Mail.Controls.Core` and run any directly affected repository tests.
- WinUI control or template: build `Wino.Mail.Controls` and the playground, then exercise the affected playground page.
- Editor C#, XAML, or web assets: build `Wino.Editor` and the playground, then verify initialization, editing/rendering, theme changes, navigation handling, and disposal as applicable.
- Public API change: search CodeGraph for consumers and build each affected project.

Do not report a UI change as verified from compilation alone. If interactive verification is unavailable, state that clearly in the handoff.

## General Constraints

- Follow the repository-level `AGENTS.md`; this file adds controls-specific guidance.
- Preserve user changes in a dirty worktree and avoid unrelated formatting churn.
- Do not edit generated `obj/` or `bin/` output.
- Avoid new NuGet packages unless the existing platform and libraries cannot solve the problem.
- Keep published cross-repository dependencies as unconditional `PackageReference` items; a sibling checkout must not change the dependency graph.
