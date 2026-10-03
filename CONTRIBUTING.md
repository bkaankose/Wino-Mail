# Contributing to Wino Mail

Wino Mail started as a personal project and grew through community interest. Contributions can include code, tests, documentation, bug reports, and proposals.

Read this guide before you start implementation. For coding-agent rules, also read [`AGENTS.md`](AGENTS.md) and any closer `AGENTS.md` file.

## Contribution policy

Create an issue before you work on a new bug or feature. If an issue already exists, comment there before you start implementation.

Create a proposal before you design a large feature or a new subsystem. Wait for maintainer approval before you start that work.

Wino preserves the direct experience of Windows Mail and Calendar. A proposal can be rejected when it conflicts with this product direction.

AI-assisted contributions are welcome. Contributors remain responsible for the design, code, tests, security, and accuracy of every submitted change.

AI-assisted changes must obey the same architecture, coding rules, and maintainer decisions as manually written changes.

## Development requirements

Wino is a packaged WinUI 3 app. Development requires Windows and the .NET SDK from [`global.json`](global.json).
Install WinApp CLI 0.7 or later and enable Windows Developer Mode.
For VS Code debugging, install Microsoft's C# extension.
Visual Studio with the .NET desktop workload is also supported.
Release Native AOT builds need the C++ build tools described in the [release guide](docs/releases.md).

## Build and run

Open the repository folder in VS Code. Select **Debug Wino Mail (packaged, x64)** and press **F5**.
The task runs `winapp run` against the app project. WinApp builds and deploys the package, then VS Code attaches the C# debugger.

For terminal use:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --detach
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug -p:Platform=x64
```

The SDK handles restore and compilation. F5 uses one PowerShell helper to skip compilation when inputs and output are unchanged.
The active app is [`src/Wino.Mail.WinUI`](src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj). Leave the deprecated UWP app unchanged.
See [development commands](docs/harness/development.md) for debugger behavior and package troubleshooting.

## Testing and maintenance

Use the [local Docker lab](tools/local-lab/README.md) for manual application testing.
Keep unit tests for logic and release-script tests for packaging.
Agent-driven UI suites and automation scripts are retired.

Use the [script index](scripts/README.md) for release packaging, lab provisioning, XAML maintenance, and localization.
Follow the verification scope in [`AGENTS.md`](AGENTS.md).

## Project architecture

Wino contains four application modes: Mail, Calendar, People, and To Do. These modes share one WinUI executable, database, service layer, and account model.

Mail synchronization supports Microsoft Graph, Gmail API, IMAP/SMTP, and POP3/SMTP. Calendar, contacts, and tasks can use provider, DAV, or local backends.

The project uses [MimeKit](https://github.com/jstedfast/MimeKit) and [MailKit](https://github.com/jstedfast/MailKit/) for MIME and standard mail protocols. Microsoft Graph supplies Microsoft integrations. Google APIs supply Google integrations.

Provider authenticators live in [`Wino.Authentication`](src/Wino.Authentication). IMAP and POP3 credentials use [`CustomServerInformation`](src/Wino.Core.Domain/Entities/Shared/CustomServerInformation.cs).

Mail actions pass through [`WinoRequestDelegator`](src/Wino.Core/Services/WinoRequestDelegator.cs) and [`WinoRequestProcessor`](src/Wino.Core/Services/WinoRequestProcessor.cs). These services prepare, batch, and send requests to the correct synchronizer.

```mermaid
flowchart LR
    UI["Wino.Mail.WinUI<br/>WinUI 3 shell, pages, controls"]
    MailVM["Wino.Mail.ViewModels<br/>mail view models"]
    CoreVM["Wino.Core.ViewModels<br/>shared settings and app view models"]
    Services["Wino.Services<br/>database, mail, folder, account services"]
    Core["Wino.Core<br/>sync, authenticators, request processing"]
    Domain["Wino.Core.Domain<br/>entities, interfaces, translations, enums"]
    Auth["Wino.Authentication<br/>OAuth helpers"]
    Messages["Wino.Messages<br/>pub-sub messages"]

    UI --> MailVM
    UI --> CoreVM
    UI --> Services
    MailVM --> Services
    CoreVM --> Services
    Services --> Domain
    Core --> Services
    Core --> Auth
    Core --> Domain
    MailVM --> Messages
    Core --> Messages
```

```mermaid
sequenceDiagram
    participant User
    participant UI as WinUI UI / ViewModel
    participant Delegator as WinoRequestDelegator
    participant Processor as WinoRequestProcessor
    participant Sync as Provider Synchronizer
    participant DB as SQLite + MIME files

    User->>UI: Delete, move, mark read, send, sync
    UI->>Delegator: Create request
    Delegator->>Processor: Validate and delegate
    Processor->>Processor: Batch with RequestComparer
    Processor->>Sync: Queue provider work
    Sync->>DB: Apply local changes through change processors
    DB-->>UI: Messenger notifications refresh UI state
```

## Project guide

- [`Wino.Mail.WinUI`](src/Wino.Mail.WinUI) contains the shell, pages, styles, activation routes, package manifest, and Windows services.
- [`Wino.Mail.ViewModels`](src/Wino.Mail.ViewModels) contains mail and application-mode view models.
- [`Wino.Calendar.ViewModels`](src/Wino.Calendar.ViewModels) contains calendar view models and calendar state.
- [`Wino.Core.ViewModels`](src/Wino.Core.ViewModels) contains shared settings and application view models.
- [`Wino.Core`](src/Wino.Core) contains synchronization, provider integrations, request processing, and change processors.
- [`Wino.Services`](src/Wino.Services) contains database, account, mail, folder, task, contact, preference, and file services.
- [`Wino.Core.Domain`](src/Wino.Core.Domain) contains shared contracts, entities, interfaces, translations, enums, and models.
- [`Wino.Authentication`](src/Wino.Authentication) contains Microsoft and Google OAuth helpers.
- [`Wino.Messages`](src/Wino.Messages) contains CommunityToolkit messenger contracts.
- [`Wino.SourceGenerators`](src/Wino.SourceGenerators) generates translation and other compile-time code.
- [`controls`](controls) contains highly customized controls shared by Wino applications. It is not a general-purpose control library.
- [`Wino.Editor`](controls/Wino.Editor) contains the HTML, CSS, and JavaScript assets for mail reading and composition.
- [`Wino.Mail.Controls.Playground`](controls/Wino.Mail.Controls.Playground) is the quick test application for controls before full application integration.
- [`tests`](tests) contains unit tests and script checks.

## Notification architecture

The package manifest defines four visible application entries: Wino Mail, Wino Calendar, Wino People, and Wino To Do. They share the main WinUI executable.

Windows identifies packaged applications with an Application User Model ID (AUMID). Each application mode needs a separate notification identity and activation route.

The [`Package.appxmanifest`](src/Wino.Mail.WinUI/Package.appxmanifest) therefore defines four hidden notification-host applications. These entries create one notification AUMID for each mode.

All four entries share one small executable, [`Wino.NotificationHost`](src/Wino.NotificationHost), which contains the activation bridge. Each entry declares its own toast COM activator class, so the class Windows calls identifies the application mode. This design keeps the toast COM activators out of the shared UI executable.

[`NotificationHostClient`](src/Wino.Mail.WinUI/Services/NotificationHostClient.cs) shows and removes toasts inside the main process. It addresses the required AUMID with `ToastNotificationManager.CreateToastNotifier(aumid)`, which Windows allows for applications in the same package. No host process starts to show or remove a notification.

Notification clicks enter the matching COM activator. Windows starts the host, which writes an activation envelope, forwards it to the main application, and exits.

[`ForwardedNotificationActivationStore`](src/Wino.Mail.WinUI/Activation/ForwardedNotificationActivationStore.cs) reads the forwarded activation. [`AppNotificationHandler`](src/Wino.Mail.WinUI/Activation/AppNotificationHandler.cs) routes it to the correct application mode.

The activation envelope format and AUMID mappings live in [`Wino.NotificationHost.Contracts`](src/Wino.NotificationHost.Contracts).

Do not register all four notification identities in the main executable. Keep the COM activation path attached to the host executable.

## Data and application state

[`WinoApplication`](src/Wino.Mail.WinUI/WinoApplication.cs) initializes application data paths. The SQLite database is `Wino200.db` in the app package's local data folder.

The database stores mail and calendar metadata. [`MimeFileService`](src/Wino.Services/MimeFileService.cs) resolves downloaded MIME files from application-local storage.

[`PreferencesService`](src/Wino.Mail.WinUI/Services/PreferencesService.cs) stores user settings and imported or exported preferences. [`StatePersistenceService`](src/Wino.Mail.WinUI/Services/StatePersistenceService.cs) stores temporary UI state.

Mail rendering and composition use [WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/) through [`Wino.Editor`](controls/Wino.Editor). Do not add a second editor asset bundle to the WinUI project.

## View models and messaging

Wino uses [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) for observable properties, commands, and messaging.

Use public partial properties with `[ObservableProperty]`. Do not annotate private backing fields.

Register messenger recipients in `RegisterRecipients()`. Unregister them in `UnregisterRecipients()`.

Messenger handlers can run outside the UI thread. Dispatch UI-bound state and WinRT work through `ExecuteUIThread(...)` or the correct dispatcher.

Dependency injection starts in [`App.xaml.cs`](src/Wino.Mail.WinUI/App.xaml.cs). Core and shared registrations live in [`CoreContainerSetup`](src/Wino.Core/CoreContainerSetup.cs) and [`ServicesContainerSetup`](src/Wino.Services/ServicesContainerSetup.cs).

Avoid new packages when the platform or repository already supplies the required function.

## Localization

For new development, add or update translations manually only in [`en_US/resources.json`](src/Wino.Core.Domain/Translations/en_US/resources.json), the English (en-US) source file.

Use the generated `Translator` properties in C# and XAML. Do not edit non-English resource files.

The maintainer generates translations for other languages from the English source before each public release.

## Controls and XAML

The `controls` projects contain highly customized controls for Wino. They are shared across Wino applications but are not general-purpose reusable libraries.

Use [`Wino.Mail.Controls.Playground`](controls/Wino.Mail.Controls.Playground) for quick control tests before integration into the full application.

Read [`controls/AGENTS.md`](controls/AGENTS.md) before you change a shared control. Format changed XAML with the repository harness before handoff.

Every icon comes from the WinoIcons fonts. To add one, follow [Add an icon](icons/README.md#add-an-icon). Do not copy an SVG into `icons/svg` by hand.

## Before you submit

1. Review the diff and remove unrelated changes.
2. Run the narrowest relevant build and tests.
3. Run the XAML and UI checks when the change affects the interface.
4. Add or update tests for changed behavior.
5. Describe what you verified and what remains unverified.

## Additional help

The project has dedicated community channels:

- [UWP Community](https://discord.gg/wNMGxYZMFy), under **Apps & Projects → wino-mail**
- [Developer Sanctuary](https://discord.gg/windows-apps-hub-714581497222398064), under **Community Projects → wino-mail**

You can also contact `bkaankose (at) outlook.com`.

## Donate

You can [donate with PayPal](https://www.paypal.com/donate/?hosted_button_id=LGPERGGXFMQ7U).
