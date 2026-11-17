# Wino.Core

The synchronization engine: one synchronizer per provider (Outlook, Gmail, IMAP, POP3, Exchange, CalDAV, CardDAV), the request pipeline, the synchronization manager, the application runtime, account setup and autodiscovery. It references `Wino.Services`, `Wino.Authentication` and `Wino.Core.Domain`, and no platform or UI project.

## The request pipeline

Every user action that changes server data takes this path. Know it before you change a request, a synchronizer or a ViewModel command.

1. **ViewModel** calls `IWinoRequestDelegator.ExecuteAsync(...)`.
2. **`WinoRequestProcessor`** turns the action into request objects, for example `MarkReadRequest`. Each request implements `ApplyUIChanges` and `RevertUIChanges`.
3. **`SynchronizationManager.QueueRequestPackAsync`** applies the UI change in memory through `RequestUiChangeCoordinator.ApplyRequests`. The UI receives a message with `EntityUpdateSource.ClientUpdated`. The request then enters the account synchronizer's queue with `QueueRequest`.
4. **`ApplicationRuntime`** receives `NewMailSynchronizationRequested` and starts the synchronizer. The synchronizer groups queued requests into bundles and runs `ExecuteNativeRequestsAsync`, which makes the network call.
5. **On success** the provider's change processor writes the database, and the storage service sends a message with `EntityUpdateSource.Server`.
6. **On failure** `RequestUiChangeCoordinator.RevertBundle` reverts the in-memory change with `EntityUpdateSource.ClientReverted`. The database is untouched.

## Hard stops

- **The database changes only after the server confirms.** Route every server-changing action through the pipeline above, with both `ApplyUIChanges` and `RevertUIChanges`. Never write a guessed result to the database, and add no offline queue and no retry queue. If a server operation needs several calls, say what state a failure between them leaves on the server.
- **Fixes to a synchronizer follow the RFC or the provider's documented API and are generic.** Add no per-server exception. For a server that misbehaves, write a note for the owner.
- **A fix must not break data that existing users have already synced.**
- **`SyncCoordinator` owns the schedule.** No other code keeps an interval constant. Google Tasks and Google contacts use a six-hour window for automatic syncs only; a manual sync always runs. Never show a quota or a rate limit to the user.
- **OAuth clients, token storage and sign-in:** read "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) first.
- **One way to build a Graph client.** Outlook uses the official `Microsoft.Graph` package; there is no generated client. Create every `GraphServiceClient` through `OutlookSynchronizer.CreateGraphClient`, which wraps the adapter in `GraphRequestAdapterWithoutBackingStore`. A client built on a plain adapter turns on change tracking for every client in the process: properties set to null are then sent as explicit nulls, and a model serialized twice sends only its changes.
- **No UI service in new code.** Do not inject a dialog or navigation contract here. Code that needs a decision returns it to the ViewModel.

## Always

- Put provider-specific behavior in that provider's synchronizer, change processor or error handler. Shared code asks the provider; it does not add a new `switch` on the provider type.
- Take `ISynchronizationManager` and `IMessenger` through the constructor in new services. The static `SynchronizationManager.Instance` and `WeakReferenceMessenger.Default` are being removed.
- Register services in `RegisterCoreServices` (`CoreContainerSetup.cs`).
- Treat code after `ConfigureAwait(false)` and every messenger recipient as background-thread code.

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

These limits exist for provider quotas and memory. Change one only with the reason stated.

| Limit | Value | Where |
| --- | --- | --- |
| Outlook Graph batch | 20 requests | `OutlookSynchronizer.cs` |
| Gmail batch | 10 requests | `GmailSynchronizer.cs` |
| IMAP new-message fetch | 200 per batch | `ImapSync/UnifiedImapSynchronizer.cs` |
| IMAP connection pool | 5 by default, 1 to 10 | `Integration/ImapClientPool.cs` |
| IMAP parallel folder sync | 3 clients | `ImapSynchronizer.cs` |
| CalDAV and CardDAV download | 50 per batch, 4 at once | the two `*DavSynchronizationEngine.cs` files |
| Default sync interval | mail 3 minutes, calendar 5 minutes | `Wino.Core.Domain/Constants.cs` |

Initial synchronization stores message identifiers and downloads MIME content on demand. Keep it that way.
