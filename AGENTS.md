# Wino Mail agent guidance

Wino Mail is a native Windows mail client. The active desktop application is the WinUI 3 project at `src/Wino.Mail.WinUI`.

Do not work in the deprecated UWP project.

## Start here

- Use `WinoMail.slnx` with Visual Studio 2022 or later.
- Use Debug and x64 for the normal development loop.
- Use CodeGraph before text search or broad file reads when locating symbols, callers, or affected tests:

```powershell
codegraph explore "describe the symbol, flow, or change"
```

- Start with the files or symbols named in the task. Keep one task focused on one coherent outcome.
- Preserve unrelated changes in a dirty worktree.

## Build and test

Restore only after package, project, target framework, or runtime-identifier inputs change:

```powershell
dotnet restore src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Use this command for a compile-only WinUI check. It does not deploy the application registered with Windows:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
```

If a task requires Release or Native AOT validation, build the app without launching it. Never deploy or test the Release package:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
```

Run the narrowest affected tests. After a successful build of the same test project, use `--no-build --no-restore` for repeated runs:

```powershell
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64 --no-restore
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64 --no-build --no-restore --filter "FullyQualifiedName~RelevantTestClass"
```

Use changed files and CodeGraph to select tests before falling back to the complete test project:

```powershell
git diff --name-only --diff-filter=ACMR | codegraph affected --stdin --quiet
```

If a WinUI build reports only `XamlCompiler.exe exited with code 1`, rerun with diagnostics and inspect the first real `WMC`, `WMC1121`, or binding error:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false "/flp:logfile=winui-build.log;verbosity=diagnostic" /bl:winui-build.binlog
```

Do not create diagnostic logs or binlogs for successful routine builds.

## Installed Debug application

Use WinApp CLI 0.6 or later in project mode for the normal development cycle. Project mode accepts the `.csproj` as input. It builds the project and activates the package with its existing manifest identity.

Before the first launch on a machine, run `winapp --version`. Verify that the installed version is 0.6 or later. Then compare the manifest identity with the installed Debug package:

```powershell
$manifest = [xml](Get-Content 'src/Wino.Mail.WinUI/Package.appxmanifest')
$identity = $manifest.Package.Identity
Get-AppxPackage -Name $identity.Name | Select-Object Name, Publisher, PackageFamilyName, InstallLocation
```

If the name and publisher match, project mode updates the same package entry. If the publisher differs, stop. Do not create another identity or registration.

Build, update the existing Debug registration, launch, and return the PID for UI automation:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --detach --json
```

When the current Debug output is already built, skip compilation for the fastest relaunch:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-build --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --detach --json
```

For launch or crash diagnosis, omit `--detach --json` and use `--debug-output`. This option keeps WinApp CLI attached. It captures first-chance exceptions and analyzes WinUI stowed exceptions after a crash. Do not attach another debugger at the same time:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --debug-output
```

Obey these package rules:

- Use the checked-in manifest and the existing package family.
- Preserve application data between deployments.
- Never use folder mode, `winapp init`, or `winapp create-debug-identity`.
- Never use `--clean` or `--unregister-on-exit`.
- Never rewrite the manifest or create a second package identity.
- Never run `Wino.Mail.WinUI.exe` directly.
- Never use `winapp run` with Release.

## WinApp UI verification

After the current x64 Debug build has been deployed and started, use WinApp CLI directly against the running process:

```powershell
winapp ui list-windows -a Wino.Mail.WinUI --json
winapp ui status -a Wino.Mail.WinUI --json
winapp ui inspect -a Wino.Mail.WinUI --interactive --depth 8 --json
```

If more than one window matches, take the stable HWND from `list-windows` and use `-w <HWND>` for every subsequent command.

Exercise the changed behavior with `winapp ui invoke`, `click`, `set-value`, `focus`, or `scroll-into-view`. Assert the result with `wait-for`, `get-value`, or `get-property`. Capture visual evidence only when layout, theme, clipping, overlap, popup, or window behavior matters:

```powershell
winapp ui wait-for "AutomationIdOrName" -a Wino.Mail.WinUI --timeout 5000
winapp ui screenshot -a Wino.Mail.WinUI --json -o artifacts\wino-ui-current.png
```

For timing-dependent or transient visual behavior, record a short bounded clip with agent-readable frames instead of taking many screenshots:

```powershell
winapp ui record -w <HWND> --duration-sec 10 --frames --fps 5 --max-edge 1280 --json -o artifacts\wino-ui-current.mp4
```

Prefer stable `AutomationProperties.AutomationId` values over localized labels. For changed XAML, run the existing static audit before UI verification:

```powershell
.\scripts\audit-xaml-automationids.ps1
```

A visible window, screenshot, or recording is not a passing interaction test. Report the action, assertion, process or HWND, and verified theme. Before testing, verify that project mode deployed the current source. Otherwise, report that UI verification is pending.

## Verification matrix

- Domain or service logic: build the affected project and run the directly affected unit tests.
- ViewModel or messenger changes: run affected unit tests, then verify the UI-bound state through the installed Debug app when behavior changed.
- XAML, code-behind, navigation, activation, windowing, or controls: redeploy the existing Debug package with WinApp CLI project mode. Use Visual Studio only when interactive debugging is required. Then run the automation-ID audit and exercise the affected flow with WinApp CLI.
- Reusable controls: follow `controls/AGENTS.md`, update the playground, and verify the relevant control states and themes.
- Localization: change only `en_US/resources.json`, build the generator output, and leave other locale files untouched.
- Package, trimming, or Native AOT work: use an explicit compile-only Release build. Never deploy, launch, or UI-test Release. Use the existing Debug registration for all runtime and UI verification.

## Architecture

```text
src/Wino.Core.Domain       Contracts, entities, translations, enums
src/Wino.Core              Synchronization, authentication, request processing
src/Wino.Services          Database, mail, folder, account, preference services
src/Wino.Core.ViewModels   Shared ViewModels
src/Wino.Mail.ViewModels   Mail ViewModels
src/Wino.Messaging         Messenger contracts
src/Wino.Mail.WinUI        Active WinUI 3 application
controls                   Reusable controls, editor, and playground
tests                      Automated tests
```

Mail requests flow through `WinoRequestDelegator`, `WinoRequestProcessor`, provider synchronizers, change processors, the local SQLite database, and messenger events. Initial synchronization queues identifiers and downloads MIME content on demand.

Register core services in `CoreContainerSetup`, shared services in `ServicesContainerSetup`, and ViewModels in `App.xaml.cs`.

Published cross-repository dependencies must use unconditional `PackageReference` items. A sibling checkout must never change the dependency graph. Publish a new NuGet version and update the centrally managed package version to consume cross-repository changes.

## Core implementation rules

- Use public partial properties with `[ObservableProperty]`.
- Do not annotate private backing fields.
- Register messenger handlers in `RegisterRecipients()` and unregister them in `UnregisterRecipients()`.
- Messenger recipients run on background threads by default. Marshal UI-bound state, collections, navigation, windows, JumpLists, and other WinRT APIs through `ExecuteUIThread(...)` or the appropriate dispatcher.
- Treat code after `ConfigureAwait(false)` as background-thread code until explicitly dispatched.
- Keep ViewModels limited to UI state and interaction. Put authentication, account API, token, preferences, and other business operations in services.
- Avoid new NuGet packages when existing platform or repository libraries are sufficient.
- Use `IWinoLogger` for errors and wrap async external operations in `try`/`catch`.
- Use logical vertical spacing (code paragraphing) in C#: separate distinct guard clauses, retrievals, transformations, UI updates, and returns with a blank line; keep expressions that form one operation together. Do not add blank lines merely to isolate every individual statement.

## WinUI and XAML rules

- Before designing a new user-facing Wino feature or changing a visual pattern, read `docs/wino-design-guideline.md`. Apply its Wino-specific layout, surfaces, command, state, accessibility, and verification decisions; update the guide when establishing a reusable new pattern.

- Do not add XAML-backed controls, flyouts, templates, or visual composition in `.xaml.cs`. Keep code-behind for handlers and view glue.
- `DataTemplate` and `ControlTemplate` do not support visual states in this project. Never put visual states inside these templates.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not constructors.
- Give every element using `x:Load` an `x:Name`.
- Do not introduce `IValueConverter` classes. Use direct WinUI conversion or existing `XamlHelpers` methods.
- `x:Bind` does not convert `double` to `GridLength`. Use an existing helper.
- Use typed `ItemTemplate` bindings and explicit `SelectedItem` for `ComboBox`.
- Do not use `DisplayMemberPath` or `SelectedValuePath`.
- Keep every shell navigation `DataTemplate` in `Styles/ShellMenu/ShellMenuTemplates.xaml` and its existing code-behind. Expose templates as public selector properties and wire them with `{StaticResource}`; do not resolve XAML resources through `Application.Current.Resources` or mutate its merged dictionaries at runtime.
- Prefer `[GeneratedDependencyProperty]` over manual dependency-property registration.
- Use command `CanExecute` and `[NotifyCanExecuteChangedFor]` instead of binding a command button's `IsEnabled` when possible.
- Use `{ThemeResource}` for visual resources and preserve Light, Dark, High Contrast, keyboard, pointer, touch, and automation behavior.
- Follow `controls/AGENTS.md` for reusable control templates, parts, playground samples, automation peers, and lifecycle rules.

## Localization, storage, and rendering

- Add English source strings only to `src/Wino.Core.Domain/Translations/en_US/resources.json`.
- Use generated `Translator` properties. XAML translation bindings use `Mode=OneTime` because `Translator` does not implement `INotifyPropertyChanged`.
- Treat non-English resource files as externally managed and do not edit them.
- SQLite data lives in the publisher cache. EML files live in app local storage and are resolved through `MimeFileService.GetMimeMessagePath()`.
- `controls/Wino.Editor` is the only reader/editor HTML, CSS, and JavaScript asset source. Preserve its document-ready bridge and on-demand MIME loading.

## Completion

Before reporting completion:

1. Review the diff for unrelated changes and generated output.
2. Run the narrowest build and tests that prove the change.
3. Run the additional verification required by the matrix above.
4. State exactly what was verified and what remains unverified.
