# Wino.Hosting

The host that runs the shared backend, up to the ViewModels, without an app. A single-file script references this project, starts a `WinoHost`, and gets the same services, runtime, database and ViewModels the apps use. No app references it and no app ships it.

## Hard stops

- **Nothing here replaces backend data.** The host supplies what a screen or an operating system would: a dispatcher, navigation, dialogs, notifications, theme, sign-in surfaces. It never substitutes a service result, a network reply or a database row. There are no mocks and no fakes.
- **Portable only.** No Windows, Apple or Android API, no platform package, and no reference to `Wino.Platform.*`, a head or `controls/`. It must build on Linux.
- **The dispatcher is real.** `HostDispatcher` is one dedicated thread with an ordered queue and a synchronization context. Keep it that way: never make `ExecuteOnUIThread` a no-op and never run queued work on the caller's thread.
- **The key file is for this host only.** `FileProtectionKeyProvider` keeps the protection key inside the data root because a host holds throwaway lab data. No app may use it; apps never fall back to an unprotected store.
- **Telemetry stays off.** `HostApplicationConfiguration` leaves the Sentry address empty so runs send nothing. Do not swap in `ApplicationConfiguration`.
- **One host per process** until the process-wide statics are removed.

## Always

- When shared code gains a contract that a host must supply, add its implementation here in the same change, or `WinoHost.StartAsync` fails for every script. The apps' registrations are the reference: `src/Wino.Mail.Android/AndroidComposition.cs` has the closest shape.
- Reuse a portable implementation from `Wino.Services` or `Wino.Authentication` before writing one here.
- A screen contract answers as dismissed or cancelled and records the call in `WinoHost.Interactions`. An interactive sign-in surface throws `NotSupportedException` with a clear message and records the attempt.
- Start through `ApplicationLifecycleCoordinator`, the same entry the apps use. Do not copy a startup order by hand.

## Using the host from a script

```csharp
#:project D:/Wino-Mail/src/Wino.Hosting/Wino.Hosting.csproj
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Hosting;

var dataRoot = args[0];   // an empty folder in your scratchpad; everything the host writes stays inside it
await using var wino = await WinoHost.StartAsync(o => o.DataRoot = dataRoot);

var discovery = wino.Get<IAutoDiscoveryService>();
var settings = await discovery.GetAutoDiscoverySettings(new AutoDiscoveryMinimalSettings { Email = args[1] });
Console.WriteLine(settings?.Domain);

foreach (var interaction in wino.Interactions)   // what the backend asked of a screen, and the host's answer
    Console.WriteLine(interaction);
```

Point `#:project` at the checkout you are working in. The data root must be empty or one a host created before.

**Synchronizing a lab account.** Lease the account first; the lab rules are in the [root guidance](../../AGENTS.md). Then, in order:

1. Build the `AccountSetupPlan` that `AccountSetupPageViewModel.BuildFinalPlan` builds for that account type.
2. Run `wino.Get<IAccountSetupRunner>().RunAsync(plan, ...)` inside `wino.Dispatcher.InvokeAsync(...)`.
3. Call `IApplicationRuntime.SynchronizeCreatedAccountAsync(account)`, then `SynchronizeAccountInboxAsync(account.Id)`.
4. Read the result back through `IFolderService` and `IMailService`, and print it with `wino.Interactions`.

This added a lab IMAP account and synchronized 12 folders and 117 inbox mails in under 3 seconds.

**Driving a ViewModel.** Do what a page does, inside `wino.Dispatcher.InvokeAsync(...)`: resolve the ViewModel, assign `viewModel.Dispatcher = wino.Dispatcher`, call `OnNavigatedTo(mode, parameter)`, then `OnPageLoaded()`. Read its properties and invoke its commands on that thread too. `MailListPageViewModel` loaded the lab inbox this way.

Never print or store the lab password or the control token.

## Performance

- The first build of a script compiles the whole backend and takes about a minute; later runs take seconds. Keep one script folder per task so its build output is reused.
- `DisposeAsync` stops the runtime, closes the database pool and the log, and stops the dispatcher. Always dispose the host, or the data root stays locked.
