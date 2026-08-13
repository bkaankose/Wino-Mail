# AGENTS.md

This file provides guidance to AI agent when working with code in this repository.

## Project Overview

Wino Mail is a native Windows mail client (Windows 10 1809+ / Windows 11) replacing the deprecated Windows Mail & Calendar. The active desktop application is the WinUI 3 project at `src/Wino.Mail.WinUI`.

## Build and Development Commands

```bash
# Open solution
# WinoMail.slnx is the main solution file (VS 2022+)

# Build WinUI project (Debug x64)
dotnet restore src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64 && dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false

# Build WinUI project with diagnostic XAML/compiler logging (use when plain build only shows "XamlCompiler.exe exited with code 1")
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false "/flp:logfile=winui-build.log;verbosity=diagnostic" /bl:winui-build.binlog

# Run tests (Debug x64)
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64

# Keep the launched app open for follow-up UIA exploration with winapp ui commands
.\scripts\winapp-smoke.ps1 -Mode Mail -KeepRunning

# Audit WinUI XAML controls for stable UI Automation selectors
.\scripts\audit-xaml-automationids.ps1

# Copilot CLI build command (Debug x64)
dotnet restore src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64 && dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
```

**Prerequisites:** Visual Studio 2022+ with ".NET desktop development" workload, .NET SDK 10+

**Startup project:** Wino.Mail.WinUI

**Platforms:** x86, x64, ARM64

## Efficient Workflow

- Start with targeted symbol or file search before reading full files
- Prefer one focused task per thread; use a new thread for unrelated follow-up work
- Keep verification narrow: build only the affected project, not the full solution, unless cross-project changes require it
- After the first restore, prefer `--no-restore` builds unless package or project references changed
- Summarize long build logs and inspect only the files named in diagnostics instead of loading large logs into context
- When the prompt already names likely files, types, or symbols, start there instead of re-mapping the repository
- If a WinUI build only reports `XamlCompiler.exe exited with code 1`, rerun with the diagnostic logging command above and inspect the terminal output plus `winui-build.log` for real `WMC`/`WMC1121`/binding diagnostics before guessing

## NuGet Dependency Policy

- Published cross-repository dependencies must always use `PackageReference`; NuGet is the source of truth.
- Never condition `PackageReference` or `ProjectReference` on whether a sibling repository or local project path exists.
- Never let the presence of a local checkout change the dependency graph or build output.
- To consume cross-repository changes, publish a new NuGet package version and update the centrally managed package version.

## Architecture

### Solution Structure
```
src/Wino.Core.Domain       → Entities, interfaces, translations, enums (shared contracts)
src/Wino.Core              → Synchronization engine, authenticators, request processing
src/Wino.Services          → Database, mail, folder, account services
src/Wino.Authentication    → OAuth2 authenticators (Outlook, Gmail)
src/Wino.Mail.ViewModels   → Mail-specific ViewModels
src/Wino.Core.ViewModels   → Shared ViewModels (settings, personalization)
src/Wino.Messaging         → Pub-sub message definitions
src/Wino.Mail.WinUI        → **Active WinUI 3 UI project** (use this)
controls/                  → Reusable editor and list controls, including the playground app
tests/                     → Automated test and smoke-test projects
```

### Mail Synchronization Flow
1. **WinoRequestDelegator** → Validates and delegates user actions (mark read, delete, move)
2. **WinoRequestProcessor** → Batches requests using RequestComparer, queues to synchronizers
3. **Synchronizers** (OutlookSynchronizer, GmailSynchronizer, ImapSynchronizer) → Execute batched operations
4. **ChangeProcessors** → Apply changes to local SQLite database
5. Database updates trigger **Messenger** events (MailAddedMessage, MailUpdatedMessage, etc.)

### Synchronizer Types
- **OutlookSynchronizer** - Microsoft Graph SDK for Office 365
- **GmailSynchronizer** - Gmail API
- **ImapSynchronizer** - MimeKit/MailKit for IMAP/SMTP

### Queue-Based Sync Pattern
- Initial sync queues mail IDs first (MailItemQueue table), downloads metadata only
- MIME content downloaded on-demand when user opens mail
- Check `MailItemFolder.IsInitialSyncCompleted` for sync state
- See QUEUE_SYNC_IMPLEMENTATION.md for details

### Dependency Injection
- `RegisterCoreServices()` in src/Wino.Core/CoreContainerSetup.cs
- `RegisterSharedServices()` in src/Wino.Services/ServicesContainerSetup.cs
- ViewModels registered in App.xaml.cs

## Key Patterns

### MVVM with Source Generators
**CORRECT - use public partial properties:**
```csharp
[ObservableProperty]
public partial string SearchQuery { get; set; } = string.Empty;
```

**WRONG - will not work:**
```csharp
[ObservableProperty]
private string searchQuery = string.Empty;
```

### Messenger Pattern
- ViewModels inherit from CoreBaseViewModel or MailBaseViewModel
- Register handlers in `RegisterRecipients()`, unregister in `UnregisterRecipients()`
- Send via `WeakReferenceMessenger.Default.Send(new MessageType(...))`
- Messenger recipients are raised from a background thread by default. In any `Receive(...)` handler, marshal UI-bound work and UI-affine WinRT APIs through `ExecuteUIThread(...)`, `DispatcherQueue.TryEnqueue(...)`, or an existing dispatcher helper before touching XAML state, navigation, windows, AppWindow/taskbar APIs, JumpList, or observable collections.

### Data Binding - No Converters
- **NEVER** create IValueConverter classes
- WinUI 3 auto-converts bool to Visibility: `Visibility="{x:Bind IsVisible, Mode=OneWay}"`
- Use XamlHelpers for complex conversions: `{x:Bind helpers:XamlHelpers.ReverseBoolToVisibilityConverter(Prop)}`
- `x:Bind` does not implicitly convert `double` to `GridLength`; when binding `RowDefinition.Height` or `ColumnDefinition.Width`, use a `XamlHelpers` method such as `DoubleToGridLength(...)`
- For `ComboBox` controls in XAML, never use `DisplayMemberPath` or `SelectedValuePath`; use a typed `ItemTemplate` and bind `SelectedItem` explicitly, preferably with `x:Bind`

### WinUI Control Authoring Standard

These rules use the `CommunityToolkit/Windows` `components/` controls as the reference design.

They apply to new reusable controls and material control changes.

Wino rules take precedence over legacy toolkit examples. For example, use generated dependency properties instead of manual registration.

Reference snapshot: [`CommunityToolkit/Windows@413892f`](https://github.com/CommunityToolkit/Windows/tree/413892f3e929beae3fbcf9863b8385e570407a41/components).

#### Control structure

- Derive from the most specific WinUI base class that supplies the required semantics.
- For a templated control, set `DefaultStyleKey = typeof(ControlType)` in the constructor.
- For a templated control, define the visual tree, default style, and `ControlTemplate` in XAML.
- Register every default style through `Themes/Generic.xaml`.
- For a large template, put its resource dictionary in the control folder and merge it from `Themes/Generic.xaml`.
- Keep behavior in partial C# files. Split large property, event, input, or automation implementations into focused files.
- Expose customization through dependency properties, data templates, styles, and template parts. Do not require visual-tree replacement for common changes.

#### Template contract

- Name every code-accessed template element `PART_{DescriptiveName}` in XAML.
- Declare each part name once. For example, use `private const string PartHeaderPresenterName = "PART_HeaderPresenter"`.
- Add `[TemplatePart(Name = PartHeaderPresenterName, Type = typeof(ContentPresenter))]` for every code-accessed part.
- Never use an unprefixed template-part name or an ad hoc string in `GetTemplateChild(...)`.
- Declare template-part fields as nullable. A custom template can omit a part.
- Resolve template parts only in `OnApplyTemplate()` or in a helper that it calls.
- Treat a missing optional part as a supported template variation.
- If a required part is missing, disable only the affected behavior and log a clear error when logging is available.

Use this template lifecycle order:

1. Detach handlers from the previous template parts.
2. Call `base.OnApplyTemplate()` exactly once.
3. Resolve each part with `GetTemplateChild(PartHeaderPresenterName) as ContentPresenter`.
4. Attach handlers to the new parts exactly once.
5. Apply property values and update visual states without transitions.

Property callbacks must tolerate calls before template application and after template replacement.

#### Dependency properties and events

- Use `[GeneratedDependencyProperty]` on a public partial property for each bindable, styleable, or animatable value.
- Use the generated property callback pattern for validation and dependent visual updates.
- Keep the dependency property as the source of truth. Do not copy its value into duplicate state.
- Use the correct default value. Never share a mutable collection as dependency-property metadata.
- Document each public property, event, enum, and control with XML comments.
- Use a routed event only when the event must bubble through the XAML tree.
- Do not register property-change callbacks each time that `OnApplyTemplate()` runs.
- If callback registration is necessary, store its token and unregister it during cleanup.

#### Visual states and themes

- Express interactive and layout modes with named visual states instead of direct code changes when practical.
- Declare every code-selected state with `[TemplateVisualState(Name = ..., GroupName = ...)]`.
- Store state and group names in constants. Use the same names in C# and XAML.
- Centralize state selection in an `UpdateVisualStates(bool useTransitions)` method when the control has multiple state inputs.
- Include applicable normal, pointer-over, pressed, disabled, focus, orientation, validation, empty, and loading states.
- Use `{ThemeResource}` values for colors, brushes, borders, and focus visuals.
- Preserve Light, Dark, and High Contrast behavior. Do not encode state with color alone.
- Keep animations interruptible. Do not make correct control state depend on animation completion.

#### Input, automation, and lifecycle

- Provide equivalent keyboard, pointer, touch, and gamepad behavior for every supported action.
- Preserve visible focus and logical tab order. Do not make a noninteractive element a tab stop.
- Prefer a semantic base class such as `ButtonBase`, `RangeBase`, or `ItemsControl` when it matches the control.
- Override `OnCreateAutomationPeer()` when the base peer does not expose the control's semantics.
- Implement the applicable UI Automation control type and provider patterns in a dedicated peer class.
- Raise automation property-change events when range, value, selection, toggle, or expand state changes.
- Respect an explicit `AutomationProperties.Name` before you calculate a fallback name.
- Mark decorative template elements with `AutomationProperties.AccessibilityView="Raw"` when they must not appear as separate content.
- Give interactive parts stable automation IDs when tests or accessibility tools must target them.
- Detach template-part handlers before template replacement.
- Detach handlers from external or long-lived event sources during unload or disposal.
- Follow the repository rule that wires XAML-backed framework and input events in XAML, not in constructors.

#### Samples and verification

- Add or update a focused playground page for every public control and important state change.
- Keep playground data local and deterministic. The control demo must not require an account or network access.
- Add tests for dependency-property defaults, callbacks, public events, and control-specific behavior.
- Apply the template twice in a test when the control owns template-part handlers. Make sure that handlers do not duplicate.
- Test keyboard behavior and the automation peer for interactive controls.
- Test custom-template behavior when template parts are optional.
- Build the control library and the playground after XAML, resource, or template changes.
- Run the XAML automation-ID audit and inspect the control in Light, Dark, and High Contrast themes.
- Do not report a control as verified from compilation alone. State which interactions and themes you inspected.

## Localization

1. Add English strings ONLY to src/Wino.Core.Domain/Translations/en_US/resources.json
2. Build project - source generators create Translator properties
3. Use Translator.{PropertyName} in code/XAML
4. Update other language files through the repository's translation workflow; they are not synchronized by an external service
5. Treat all non-en_US translation files as managed externally and leave them untouched, even when adding new localization keys
6. In XAML, translation bindings must use `Mode=OneTime` because `src/Wino.Core.Domain/Translator.cs` does not implement `INotifyPropertyChanged`

## Storage

- **SQLite database** in publisher cache folder (shared with future Wino Calendar)
- **EML files** in app local storage, referenced by `MailCopy.FileId`
- Paths resolved via `MimeFileService.GetMimeMessagePath()`

## WebView2 Mail Rendering

- `controls/Wino.Editor` owns the reusable `WinoMailRenderer` and `WinoMailEditor` WinUI controls
- Reader/editor HTML, CSS, and JavaScript are embedded in `controls/Wino.Editor`; do not add a second asset bundle to `src/Wino.Mail.WinUI`
- WebView2 readiness is signaled by the embedded document bridge before rendering or editing commands run
- MIME content is downloaded on-demand, not during sync

## Common Pitfalls

- Forgetting to register ViewModels in App.xaml.cs `RegisterViewModels()`
- Not calling `RegisterRecipients()` for message handlers
- Using private fields with `[ObservableProperty]` instead of public partial
- Creating IValueConverter classes instead of using XamlHelpers
- Editing UWP project files instead of WinUI equivalents
- Hardcoding strings instead of using Translator
- Forgetting to unregister Messenger recipients (memory leaks)
- Putting authentication validation, token refresh, account API calls, settings serialization/deserialization, or preference-application logic into ViewModels instead of the corresponding service

## Code Style

- Avoid introducing new NuGet packages when possible
- Use existing libraries (MimeKit, MailKit, Microsoft Graph, Gmail API)
- Use `var` where type is obvious
- String interpolation over string.Format
- Wrap async operations in try-catch
- Log errors via IWinoLogger
- Reusable controls in `Wino.Mail.Controls` must be named `Wino{ControlName}`, live in a matching `{ControlName}/` folder, and use the matching `Wino.Mail.Controls.{ControlName}` namespace. Keep control-specific supporting files in that folder as well.
- For dependency properties in WinUI code, always prefer `[GeneratedDependencyProperty]` from CommunityToolkit over manual `DependencyProperty.Register(...)` declarations.
- When a `[RelayCommand]` needs enable/disable logic, prefer the command's `CanExecute` over binding `Button.IsEnabled` in XAML; use `[NotifyCanExecuteChangedFor]` on dependent properties and call `NotifyCanExecuteChanged()` explicitly when non-generated state affects the command.
- In ViewModels, update all UI-bound properties/collections via `ExecuteUIThread(...)` (especially after awaited calls and any use of `ConfigureAwait(false)`).
- `ConfigureAwait(false)` continues execution on a background thread. Any UI-bound property change, `INotifyPropertyChanged` notification, collection mutation, or similar UI-facing state update after that point must be marshaled back with `ExecuteUIThread(...)` or the appropriate dispatcher call, otherwise the app can crash.
- Messenger messages are raised from a background thread by default, while UI control event handlers such as `Button.Click` start on the UI thread. Never assume a `Receive(...)` handler is on the UI thread; dispatch before calling UI-affine WinRT APIs such as `JumpList.LoadCurrentAsync()`, taskbar/window APIs, navigation, or before updating UI-bound state.
- ViewModels should only handle UI interaction/state and delegate business logic to services; account-management work belongs in `WinoAccountProfileService`, and preferences import/export/apply logic belongs in `PreferencesService`.
- In `EventDetailsPageViewModel.LoadAttendeesAsync`, never mutate `CurrentEvent.Attendees` outside `ExecuteUIThread(...)`.
- Never create pure C# controls or controls that heavily manipulate UI structure from `.cs` files. Define controls in XAML and keep UI composition in XAML.
- Never add XAML-backed UI controls to `.xaml.cs`. If a view has XAML, all control declarations, flyouts, templates, and visual composition belong in the `.xaml` file; keep `.xaml.cs` limited to event handling and view glue.
- Never subscribe to framework events like `Loaded`, `Unloaded`, or input events from constructors in `.xaml.cs` for XAML-backed controls and pages; wire them directly in XAML instead.
- If you use `x:Load` in XAML, always give that `UIElement` an `x:Name`.



