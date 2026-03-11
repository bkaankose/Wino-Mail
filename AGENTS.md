# AGENTS.md

This file provides guidance to AI agent when working with code in this repository.

## Project Overview

Wino Mail is a native Windows mail client (Windows 10 1809+ / Windows 11) replacing the deprecated Windows Mail & Calendar. It's **transitioning from UWP to WinUI 3** - always work with WinUI projects (Wino.Mail.WinUI), never edit the deprecated Wino.Mail UWP project.

## Build and Development Commands

```bash
# Open solution
# WinoMail.slnx is the main solution file (VS 2022+)

# Build WinUI project (Debug x64)
dotnet restore Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64 && dotnet build Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false

# Run tests (Debug x64)
dotnet test Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64

# Copilot CLI build command (Debug x64)
dotnet restore Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64 && dotnet build Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
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
Wino.Mail.WinUI        → **Active WinUI 3 UI project** (use this)
Wino.Mail              → **Deprecated UWP project** (DO NOT EDIT)
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

### Data Binding - No Converters
- **NEVER** create IValueConverter classes
- WinUI 3 auto-converts bool to Visibility: `Visibility="{x:Bind IsVisible, Mode=OneWay}"`
- Use XamlHelpers for complex conversions: `{x:Bind helpers:XamlHelpers.ReverseBoolToVisibilityConverter(Prop)}`

## Localization

1. Add English strings ONLY to Wino.Core.Domain/Translations/en_US/resources.json
2. Build project - source generators create Translator properties
3. Use Translator.{PropertyName} in code/XAML
4. NEVER edit any resources.json file outside Wino.Core.Domain/Translations/en_US/resources.json
5. Treat all non-en_US translation files as managed externally and leave them untouched, even when adding new localization keys

## Storage

- **SQLite database** in publisher cache folder (shared with future Wino Calendar)
- **EML files** in app local storage, referenced by `MailCopy.FileId`
- Paths resolved via `MimeFileService.GetMimeMessagePath()`

## WebView2 Mail Rendering

- `reader.html` for reading mails, `editor.html` for composing (Jodit editor)
- Virtual host mapping: `https://wino.mail/reader.html`
- JavaScript interop via `ExecuteScriptFunctionAsync()`
- MIME content downloaded on-demand, not during sync

## Common Pitfalls

- Forgetting to register ViewModels in App.xaml.cs `RegisterViewModels()`
- Not calling `RegisterRecipients()` for message handlers
- Using private fields with `[ObservableProperty]` instead of public partial
- Creating IValueConverter classes instead of using XamlHelpers
- Editing UWP project files instead of WinUI equivalents
- Hardcoding strings instead of using Translator
- Forgetting to unregister Messenger recipients (memory leaks)

## Code Style

- Avoid introducing new NuGet packages when possible
- Use existing libraries (MimeKit, MailKit, Microsoft Graph, Gmail API)
- Use `var` where type is obvious
- String interpolation over string.Format
- Wrap async operations in try-catch
- Log errors via IWinoLogger
- For dependency properties in WinUI code, always prefer `[GeneratedDependencyProperty]` from CommunityToolkit over manual `DependencyProperty.Register(...)` declarations.
- In ViewModels, update all UI-bound properties/collections via `ExecuteUIThread(...)` (especially after awaited calls and any use of `ConfigureAwait(false)`).
- In `EventDetailsPageViewModel.LoadAttendeesAsync`, never mutate `CurrentEvent.Attendees` outside `ExecuteUIThread(...)`.
- Never create pure C# controls or controls that heavily manipulate UI structure from `.cs` files. Define controls in XAML and keep UI composition in XAML.
- For XAML-backed controls and pages, wire framework events like `Loaded`, `Unloaded`, and input events from XAML, not by subscribing in constructors in `.xaml.cs`.



