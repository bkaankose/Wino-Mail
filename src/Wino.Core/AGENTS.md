# Wino.Core

What every mail provider shares: the synchronizer base classes, the request pipeline, the provider registry, the synchronization manager, the application runtime, account setup and autodiscovery. It references `Wino.Services`, `Wino.Authentication` and `Wino.Core.Domain`. It references no provider project and no platform or UI project.

Each provider lives in its own project and has its own file: [Outlook](../Wino.Providers.Outlook/AGENTS.md), [Gmail](../Wino.Providers.Gmail/AGENTS.md), [IMAP and POP3](../Wino.Providers.Imap/AGENTS.md), [Exchange](../Wino.Providers.Exchange/AGENTS.md), [CalDAV and CardDAV](../Wino.Providers.Dav/AGENTS.md).

## The request pipeline

Every user action that changes server data takes this path. Know it before you change a request, a synchronizer or a ViewModel command.

1. **ViewModel** calls `WinoRequestCoordinator` (in `Wino.Mail.ViewModels`). It asks any question first (confirm a permanent delete, pick a move target, name a folder), then calls `IWinoRequestDelegator` with the answer as data, and shows the error if the backend returns one. The backend never asks.
2. **`WinoRequestProcessor`** turns the action into request objects, for example `MarkReadRequest`. Each request implements `ApplyUIChanges(IUIMessageSender)` and `RevertUIChanges(IUIMessageSender)`; the pipeline hands it the sender, because a request is created with `new` and cannot be injected.
3. **`SynchronizationManager.QueueRequestPackAsync`** applies the UI change in memory through `RequestUiChangeCoordinator.ApplyRequests`. The UI receives a message with `EntityUpdateSource.ClientUpdated`. The request then enters the account synchronizer's queue with `QueueRequest`.
4. **`ApplicationRuntime`** receives `NewMailSynchronizationRequested` and starts the synchronizer. The synchronizer groups queued requests into bundles and runs `ExecuteNativeRequestsAsync`, which makes the network call.
5. **On success** the provider's change processor writes the database, and the storage service sends a message with `EntityUpdateSource.Server`.
6. **On failure** `RequestUiChangeCoordinator.RevertBundle` reverts the in-memory change with `EntityUpdateSource.ClientReverted`. The database is untouched.

While the app lock is on and the window is locked, `SynchronizationManager.QueueRequestPackAsync` and `WinoRequestDelegator` refuse user requests with `AppLockedException`. Synchronization keeps running. A notification action the user kept runs inside `IAppLockService.AllowActionsWhileLocked()`.

## The provider registry

Shared code never names a provider's classes and never switches on the provider type. It asks `IMailProviderRegistry`:

- A yes-or-no or enum difference between providers is data on `MailProviderTraits` (for example `SupportsFocusedInbox`, `StoresMessageInSeveralFolders`, `UsesQuotaWindowForTasksAndContacts`).
- Behavior is a member of `IMailProviderModule`: create the authenticator, create the synchronizer, decide whether an existing synchronizer still fits, recognize an authorization failure.
- Each provider project exposes one `Add...Provider` method that registers its module and its own services. Every host calls all five directly after `RegisterCoreServices()`, Gmail first, because the push relay builders depend on that order.

Keep the module contract small. Add a member only when shared code would otherwise have to name a provider.

## Hard stops

- **The database changes only after the server confirms.** Route every server-changing action through the pipeline above, with both `ApplyUIChanges` and `RevertUIChanges`. Never write a guessed result to the database, and add no offline queue and no retry queue. If a server operation needs several calls, say what state a failure between them leaves on the server.
- **No reference to a provider project**, here or in `Wino.Services`, `Wino.Authentication`, `Wino.Core.Domain` or `Wino.Mail.ViewModels`. A provider project never references another provider project. When two providers need the same code, it lives here.
- **Fixes to a synchronizer follow the RFC or the provider's documented API and are generic.** Add no per-server exception. For a server that misbehaves, write a note for the owner.
- **A fix must not break data that existing users have already synced.**
- **`SyncCoordinator` owns the schedule.** No other code keeps an interval constant. Never show a quota or a rate limit to the user.
- **OAuth clients, token storage and sign-in:** read "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) first.
- **No UI contract below the ViewModels.** `WINOUI001` fails the build when a dialog, navigation, theme or dispatcher contract is used in this project or a provider project. Code that needs a decision takes the answer as data on the request, or returns a typed result such as `MailOperationDecisionRequiredException` to the ViewModel. A notice goes out as `InfoBarMessageRequested` through `IMessenger`.

## Always

- Take `ISynchronizationManager`, `IMessenger` and `RequestUiChangeCoordinator` through the constructor. There is no static sync manager, and `WINOMSG001` fails the build on `WeakReferenceMessenger.Default` in a shared project.
- Give every synchronizer, error handler and request executor the one `RequestUiChangeCoordinator` singleton from the container. A second instance does not know which changes were applied, so a revert would be skipped.
- Register shared services in `RegisterCoreServices` (`CoreContainerSetup.cs`). A service only one provider needs is registered in that provider's `Add...Provider` method.
- The contracts that need MimeKit or MailKit live in `Interfaces/`, in the `Wino.Core.Interfaces` namespace.
- Treat code after `ConfigureAwait(false)` and every messenger recipient as background-thread code.

Shared code that looks provider-specific but stays here, because more than one provider uses it:

- `CalDavChangeProcessor`, `CalendarIcsBuilder` and `CalDavAttachmentRows`: the calendar ICS store that IMAP, DAV and local calendars share.
- `MailKitSmtpTransport`, its connection policy and `MailKitOAuthAuthenticator`: IMAP, Exchange and MAPI send through them.
- `WinoProtocolLogger`: a ViewModel reads its log folder.
- `OutlookDeclineEventRequest`: created by the shared delegator and checked by Gmail.

## Running a change

A service that needs only the network runs from a single-file script. This one runs the real autodiscovery for an address; it printed IMAP and SMTP settings and both DAV endpoints for a public provider in 8 seconds on a warm build:

```csharp
#:project D:/Wino-Mail/src/Wino.Core/Wino.Core.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Text.Json;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Services;

var address = args[0];
var discovery = new AutoDiscoveryService(new DnsLookupService());

var settings = await discovery.GetAutoDiscoverySettings(new AutoDiscoveryMinimalSettings { Email = address });
Console.WriteLine(settings is null ? "mail: nothing found" : "mail: " + JsonSerializer.Serialize(settings));
Console.WriteLine("caldav:  " + await discovery.DiscoverCalDavServiceUriAsync(address));
Console.WriteLine("carddav: " + await discovery.DiscoverCardDavServiceUriAsync(address));
```

How to run a script, and the rules for it, are under "Running backend code without an app" in the [root guidance](../../AGENTS.md).

A synchronizer or a request runs from a script through the host: [Wino.Hosting](../Wino.Hosting/AGENTS.md) adds a leased lab account with the app's own setup path and synchronizes it against the real server. Use that for every change to a synchronizer, a request or the runtime, then check user-visible behavior in the Windows Debug app as the root guidance describes.

## Performance

- The default sync interval is 3 minutes for mail and 5 minutes for calendar (`Wino.Core.Domain/Constants.cs`). Batch sizes and connection limits are in each provider's file.
- Initial synchronization stores message identifiers and downloads MIME content on demand. Keep it that way.
