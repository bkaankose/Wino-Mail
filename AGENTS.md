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

Visual Studio owns the Wino Mail development package lifecycle.

1. Open `WinoMail.slnx` in Visual Studio.
2. Select `Wino.Mail.WinUI` as the startup project.
3. Select Debug and x64.
4. Use Visual Studio to build, deploy, register, and start the application.
5. Redeploy after any source or resource change that must be verified in the running UI.

Agents can test only the registered Debug package. Never create a side-by-side package or rewrite `AppxManifest.xml`.

Never register a loose package layout or create an app entry named `Wino Mail (Debug)`.

Do not use `winapp run`, `winapp create-debug-identity`, or `winapp unregister` for Wino Mail. Those commands change package registration. Do not run `Wino.Mail.WinUI.exe` directly.

WinApp CLI 0.3.1 does not activate an already registered package or attach its debug-output collector to an existing process. If the Visual Studio-deployed application is not running, ask the user to start it from Visual Studio or Windows. Use Visual Studio for managed/native crash debugging.

## WinApp UI verification

After Visual Studio has deployed and started the current Debug build, use WinApp CLI directly against the running process:

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

Prefer stable `AutomationProperties.AutomationId` values over localized labels. For changed XAML, run the existing static audit before UI verification:

```powershell
.\scripts\audit-xaml-automationids.ps1
```

A visible window or screenshot alone is not a passing interaction test. Report the action, assertion, process or HWND, and theme that were verified. If Visual Studio has not deployed the current changes, state that UI verification is pending rather than testing a stale package.

## Verification matrix

- Domain or service logic: build the affected project and run the directly affected unit tests.
- ViewModel or messenger changes: run affected unit tests, then verify the UI-bound state through the installed Debug app when behavior changed.
- XAML, code-behind, navigation, activation, windowing, or controls: compile and redeploy with Visual Studio.
  Then run the automation-ID audit and exercise the affected flow with WinApp CLI.
- Reusable controls: follow `controls/AGENTS.md`, update the playground, and verify the relevant control states and themes.
- Localization: change only `en_US/resources.json`, build the generator output, and leave other locale files untouched.
- Package, trimming, or Native AOT work: use the explicit Release workflow for that task.
  Do not add this work to the routine Debug loop.

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

## WinUI and XAML rules

- Do not add XAML-backed controls, flyouts, templates, or visual composition in `.xaml.cs`. Keep code-behind for handlers and view glue.
- Wire XAML-backed `Loaded`, `Unloaded`, and input events in XAML, not constructors.
- Give every element using `x:Load` an `x:Name`.
- Do not introduce `IValueConverter` classes. Use direct WinUI conversion or existing `XamlHelpers` methods.
- `x:Bind` does not convert `double` to `GridLength`. Use an existing helper.
- Use typed `ItemTemplate` bindings and explicit `SelectedItem` for `ComboBox`.
- Do not use `DisplayMemberPath` or `SelectedValuePath`.
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
