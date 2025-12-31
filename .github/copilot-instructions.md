# Copilot Instructions for Wino-Mail Project

## Project Overview

Wino Mail is a native Windows mail client targeting Windows 10 1809+ and Windows 11. The project is **transitioning from UWP to WinUI 3** - always work with WinUI projects (Wino.Mail.WinUI, Wino.Core.WinUI), never edit the old Wino.Mail UWP project.

### Key Technologies
- **WinUI 3** for UI (previously UWP/WinUI 2)
- **MVVM Toolkit** (CommunityToolkit.Mvvm) for ViewModels with source generators
- **Messenger** pattern (WeakReferenceMessenger.Default) for event pub-sub throughout the codebase
- **SQLite** database stored in publisher cache folder (not local storage)
- **WebView2** for mail rendering/composition with custom HTML/JavaScript editors
- **MimeKit/MailKit** for IMAP/SMTP operations
- **Microsoft Graph SDK** for Outlook synchronization
- **Gmail API** for Gmail synchronization

### Solution Structure
```
Wino.Core.Domain       → Entities, interfaces, translations, enums (shared contracts)
Wino.Core              → Synchronization engine, authenticators, request processing
Wino.Services          → Database, mail, folder, account services
Wino.Mail.ViewModels   → Mail-specific ViewModels
Wino.Core.ViewModels   → Shared ViewModels (settings, personalization)
Wino.Mail.WinUI        → **Active WinUI 3 UI project** (use this)
Wino.Mail              → **Deprecated UWP project** (DO NOT EDIT)
```

## Architecture Patterns

### Mail Synchronization Flow
1. **WinoRequestDelegator** → Validates and delegates user actions (mark read, delete, move)
2. **WinoRequestProcessor** → Batches requests using RequestComparer, queues to synchronizers
3. **Synchronizers** (OutlookSynchronizer, GmailSynchronizer, ImapSynchronizer) → Execute batched operations
4. **ChangeProcessors** (OutlookChangeProcessor, etc.) → Apply changes to local database
5. Database updates trigger **Messenger** events (MailAddedMessage, MailUpdatedMessage, etc.)

### Queue-Based Sync (New Pattern - See QUEUE_SYNC_IMPLEMENTATION.md)
- Initial sync now queues mail IDs first (MailItemQueue table), downloads metadata only (no MIME)
- MIME content downloaded on-demand when user opens mail
- Synchronizers override `QueueMailIdsForInitialSyncAsync()`, `DownloadMailsFromQueueAsync()`, `CreateMinimalMailCopyAsync()`
- Check `MailItemFolder.IsInitialSyncCompleted` to determine sync state

### Dependency Injection Setup
Services registered in extension methods across projects:
- `RegisterCoreServices()` in Wino.Core/CoreContainerSetup.cs
- `RegisterSharedServices()` in Wino.Services/ServicesContainerSetup.cs
- `RegisterCoreUWPServices()` in CoreUWPContainerSetup.cs
- ViewModels registered in App.xaml.cs with AddTransient/AddSingleton

### Messenger Pattern (Event Pub-Sub)
- All ViewModels inherit from CoreBaseViewModel or MailBaseViewModel which implement IRecipient<T>
- Register/unregister message handlers in `RegisterRecipients()` / `UnregisterRecipients()`
- Send messages via `WeakReferenceMessenger.Default.Send(new MessageType(...))`
- Common messages: MailAddedMessage, MailUpdatedMessage, NavigationRequested, ThemeChanged

## ViewModels Development Guidelines

### Observable Properties - Critical Pattern
- **ALWAYS** use `public partial` observable properties with MVVM Toolkit source generators
- **NEVER** use private fields with `[ObservableProperty]` attribute
- **Correct:**
  ```csharp
  [ObservableProperty]
  public partial string SearchQuery { get; set; } = string.Empty;
  ```
- **Incorrect:**
  ```csharp
  [ObservableProperty]
  private string searchQuery = string.Empty;  // WRONG - will not work
  ```

### ViewModels Structure
- Inherit from MailBaseViewModel (for mail features) or CoreBaseViewModel (for shared features)
- Use `[RelayCommand]` for command methods - source generator creates Command properties
- Implement IRecipient<TMessage> for message handlers
- Use `IMailDialogService` for Mail-related dialogs, `IDialogServiceBase` for core dialogs
- Call `RegisterRecipients()` in constructor/OnNavigatedTo, `UnregisterRecipients()` in OnNavigatedFrom

## Localization System

### Translation Workflow (Custom T4-based System)
1. Add English strings ONLY to `Wino.Core.Domain/Translations/en_US/resources.json`
2. Build the project - source generators automatically create Translator properties
3. Use `Translator.{PropertyName}` in ViewModels, XAML (with x:Bind, OneTime mode)
4. **NEVER** edit other language files - Crowdin manages translations automatically
5. **NEVER** hardcode user-facing strings

### Usage Examples
```csharp
// ViewModel
_dialogService.InfoBarMessage(Translator.Info_MissingFolderTitle, message);

// XAML
<TextBlock Text="{x:Bind Translator.Settings_Title, Mode=OneTime}" />
```

## UI Data Binding and Converters

### WinUI 3 Automatic Conversions
- **NEVER** create IValueConverter classes or add them to Converters.xaml
- **NEVER** use BoolToVisibilityConverter - WinUI 3 SDK automatically converts bool to Visibility
- Direct binding: `Visibility="{x:Bind IsVisible, Mode=OneWay}"`

### XamlHelpers for Complex Conversions
- **ALWAYS** use XamlHelpers static methods instead of converters
- Add xmlns: `xmlns:helpers="using:Wino.Helpers"`
- Usage: `{x:Bind helpers:XamlHelpers.ReverseBoolToVisibilityConverter(PropertyName), Mode=OneWay}`
- Available methods: ReverseBoolToVisibilityConverter, CountToBooleanConverter, BoolToSelectionMode, Base64ToBitmapImage
- Add new methods to XamlHelpers.cs when needed, don't create converters

## WebView2 Mail Rendering

### Architecture
- **reader.html** (Wino.Mail.WinUI/JS/) for reading mails
- **editor.html** for composing mails (uses Jodit editor, not Quill as originally planned)
- WebView2 uses virtual host mapping: `https://wino.mail/reader.html`
- JavaScript interop via `ExecuteScriptFunctionAsync()` to call functions like `RenderHTML()`
- MIME content downloaded on-demand, not during sync

### Key Patterns
- Set environment variables for WebView2 before initialization (overlay scrollbars, cache)
- Wait for DOMContentLoaded event before script execution
- Handle theme changes by updating editor CSS dynamically
- Cancel external navigation, open in browser via Launcher.LaunchUriAsync()

## File Structure and Project Organization

### Critical Rules
- **NEVER** edit files in Wino.Mail (UWP) project - it's deprecated
- **ALWAYS** work with Wino.Mail.WinUI for UI components
- Place ViewModels in Wino.Mail.ViewModels (mail-specific) or Wino.Core.ViewModels (shared)
- Create abstract base classes in Views/Abstract folders
- Mail-specific dialog services go in Wino.Mail.WinUI/Services

### Database and Storage
- SQLite database in publisher cache folder (not app local storage)
- EML files stored in app local storage, referenced by MailCopy.FileId
- Paths resolved via MimeFileService.GetMimeMessagePath()
- Database entities in Wino.Core.Domain/Entities

## Error Handling and User Feedback

### Exception Handling Patterns
```csharp
try {
    await operation();
} catch (UnavailableSpecialFolderException ex) {
    _dialogService.InfoBarMessage(title, message, InfoBarMessageType.Warning, buttonText, action);
} catch (NotImplementedException) {
    _dialogService.ShowNotSupportedMessage();
}
```

### Dialog Service Methods
- `InfoBarMessage()` - simple notifications with optional action button
- `ShowConfirmationDialogAsync()` - yes/no dialogs
- `PickFilesAsync()` - file selection
- Always check for null/empty results from dialog operations

## Code Style and Best Practices

- Use `var` where type is obvious from right side
- String interpolation over string.Format for simple cases
- Keep methods focused and single-responsibility
- Add XML documentation for public APIs
- Avoid introducing new NuGet packages - maximize use of existing libraries
- Wrap async operations in try-catch blocks
- Log errors via IWinoLogger but don't expose technical details to users

## Development Workflow

### Building and Running
- Open WinoMail.slnx in Visual Studio 2022+
- Target platforms: x86, x64, ARM64 (ARM32 being phased out)
- Minimum: Windows 10 1809, Target: Windows 11 22H2
- Set Wino.Mail.WinUI as startup project

### Testing
- Test suite in Wino.Core.Tests
- Manual testing required for UI/WebView2 interactions
- Test synchronization with real accounts when modifying synchronizers

### Common Pitfalls
- Forgetting to register ViewModels in App.xaml.cs RegisterViewModels()
- Not calling RegisterRecipients() for message handlers
- Using private fields with [ObservableProperty] (won't work - must be public partial)
- Creating IValueConverter classes instead of using XamlHelpers
- Editing UWP project files instead of WinUI equivalents
- Hardcoding strings instead of using Translator
- Forgetting to unregister Messenger recipients (memory leaks)
