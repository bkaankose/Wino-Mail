# AGENTS.md

This file provides guidance to AI agent when working with code in this repository.

## Project Overview

Wino Mail is a native Windows mail client replacing the deprecated Windows Mail & Calendar. It is being reverse-migrated from WinUI 3 to a .NET 10 UWP/WinUI 2 UI (`Wino.Mail.Uwp`) with a packaged WinForms companion (`Wino.Companion`). New UI and lifecycle work belongs in the UWP and companion projects; the WinUI 3 project is retained only as a migration reference until removal.

## Build and Development Commands

```bash
# Open solution
# WinoMail.slnx is the main solution file (VS 2022+)

# Build the .NET 10 UWP UI (Debug x64). Use Visual Studio MSBuild;
# dotnet build does not execute the UWP XAML targets correctly.
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" Wino.Mail.Uwp\Wino.Mail.Uwp.csproj /restore /p:Configuration=Debug /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false

# Build the WAP/MSIX package containing UWP + companion.
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" Wino.Packaging\Wino.Packaging.wapproj /restore /p:Configuration=Debug /p:Platform=x64 /p:AppxPackageSigningEnabled=false /p:GenerateAppxPackageOnBuild=true /p:AppxBundle=Never

# Run tests (Debug x64)
dotnet test Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64

# Keep the launched app open for follow-up UIA exploration with winapp ui commands
.\scripts\winapp-smoke.ps1 -Mode Mail -KeepRunning

# Audit WinUI XAML controls for stable UI Automation selectors
.\scripts\audit-xaml-automationids.ps1

# Build the companion alone (Debug x64)
dotnet build Wino.Companion\Wino.Companion.csproj -c Debug /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

**Prerequisites:** Visual Studio 2022+ with ".NET desktop development" workload, .NET SDK 10+

**Startup project:** Wino.Packaging (deploys `Wino.Mail.Uwp` and `Wino.Companion`)

**Platforms:** x86, x64, ARM64

## Efficient Workflow

- Start with targeted symbol or file search before reading full files
- Prefer one focused task per thread; use a new thread for unrelated follow-up work
- Keep verification narrow: build only the affected project, not the full solution, unless cross-project changes require it
- After the first restore, prefer `--no-restore` builds unless package or project references changed
- Summarize long build logs and inspect only the files named in diagnostics instead of loading large logs into context
- When the prompt already names likely files, types, or symbols, start there instead of re-mapping the repository
- If a WinUI build only reports `XamlCompiler.exe exited with code 1`, rerun with the diagnostic logging command above and inspect the terminal output plus `winui-build.log` for real `WMC`/`WMC1121`/binding diagnostics before guessing

## Architecture

### Solution Structure
```
Wino.Core.Domain       → Entities, interfaces, translations, enums (shared contracts)
Wino.Core              → Synchronization engine, authenticators, request processing
Wino.Services          → Database, mail, folder, account services
Wino.Authentication    → OAuth2 authenticators (Outlook, Gmail)
Wino.Mail.ViewModels   → Mail-specific ViewModels
Wino.Core.ViewModels   → Shared ViewModels (settings, personalization)
Wino.Messaging         → Pub-sub message definitions
Wino.Mail.Uwp          → **Active .NET 10 UWP / WinUI 2 UI project**
Wino.Companion         → **Packaged WinForms tray/synchronization/authentication process**
Wino.Packaging         → **WAP/MSIX packaging and deployment project**
Wino.Mail.WinUI        → Migration reference only; do not add new implementation
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
- `RegisterCoreServices()` in Wino.Core/CoreContainerSetup.cs
- `RegisterSharedServices()` in Wino.Services/ServicesContainerSetup.cs
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

## Localization

1. Add English strings ONLY to Wino.Core.Domain/Translations/en_US/resources.json
2. Build project - source generators create Translator properties
3. Use Translator.{PropertyName} in code/XAML
4. NEVER edit any resources.json file outside Wino.Core.Domain/Translations/en_US/resources.json
5. Treat all non-en_US translation files as managed externally and leave them untouched, even when adding new localization keys
6. In XAML, translation bindings must use `Mode=OneTime` because `Wino.Core.Domain/Translator.cs` does not implement `INotifyPropertyChanged`

## Storage

- **SQLite database** in publisher cache folder (shared with future Wino Calendar)
- **EML files** in app local storage, referenced by `MailCopy.FileId`
- Paths resolved via `MimeFileService.GetMimeMessagePath()`

## WebView2 Mail Rendering

- `Wino.Mail.Editor` owns packaged editor/renderer assets and builds self-contained documents from `ms-appx` content; it does not extract assets into LocalCache.
- Use `WinoMailEditor` for composition and `WinoMailRenderer` for message/calendar rendering.
- DarkReader and Linkify are embedded dependencies; MIME content remains downloaded on demand.

## Common Pitfalls

- Forgetting to register ViewModels in App.xaml.cs `RegisterViewModels()`
- Not calling `RegisterRecipients()` for message handlers
- Using private fields with `[ObservableProperty]` instead of public partial
- Creating IValueConverter classes instead of using XamlHelpers
- Adding new implementation to the legacy WinUI 3 project instead of the UWP/companion targets
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



