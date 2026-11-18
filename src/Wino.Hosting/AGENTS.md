# Wino.Hosting

The host that runs the shared backend, up to the ViewModels, without an app. A single-file script references this project, starts a `WinoHost`, and gets the same services, runtime, database and ViewModels the apps use. No app references it and no app ships it.

## Hard stops

- **Nothing here replaces backend data.** The host supplies what a screen or an operating system would: a dispatcher, navigation, dialogs, notifications, theme, sign-in surfaces. It never substitutes a service result, a network reply or a database row. There are no mocks and no fakes.
- **Portable only.** No Windows, Apple or Android API, no platform package, and no reference to `Wino.Platform.*`, a head or `controls/`. It must build on Linux.
- **The dispatcher is real.** `HostDispatcher` is one dedicated thread with an ordered queue and a synchronization context. Keep it that way: never make `ExecuteOnUIThread` a no-op and never run queued work on the caller's thread.
- **The key file is for this host only.** `FileProtectionKeyProvider` keeps the protection key inside the data root because a host holds throwaway lab data. No app may use it; apps never fall back to an unprotected store.
- **Telemetry stays off.** `HostApplicationConfiguration` leaves the Sentry address empty so runs send nothing. Do not swap in `ApplicationConfiguration`.
- **One host per process, unless you only need separate data.** Two hosts in one process keep separate messengers, sync managers, databases, accounts and preferences, and a message sent in one does not reach the other. Logging is still shared: the second host takes over the log file, and disposing one host closes the logger for both.

## Always

- When shared code gains a contract that a host must supply, add its implementation here in the same change, or `WinoHost.StartAsync` fails for every script. The apps' registrations are the reference: `src/Wino.Mail.Android/AndroidComposition.cs` has the closest shape.
- Reuse a portable implementation from `Wino.Services` or `Wino.Authentication` before writing one here. The ones every non-Windows host shares come from `AddPortablePlatformServices`.
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

foreach (var interaction in wino.Interactions)   // what ViewModels asked of a screen, and the host's answer
    Console.WriteLine(interaction);
```

Point `#:project` at the checkout you are working in. The data root must be empty or one a host created before.

**A lab account, a sync and a ViewModel.** Lease the account first; the lab rules are in the [root guidance](../../AGENTS.md).

```csharp
var lease = LabLease.Load(args[1]);                                   // the lease file from lab.ps1 lease
var account = await wino.AddImapAccountAsync(lease.Account("stalwart"), LabAccountModes.AllThroughDav);
await wino.SynchronizeAsync(account);                                 // full sync of every granted mode

var inbox = await wino.Get<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox);
var folderLoad = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var parameter = new NavigateMailFolderEventArgs(new FolderMenuItem(inbox, account, null), folderLoad);

var list = await wino.OpenAsync<MailListPageViewModel>(parameter);   // what a page does, on the dispatcher thread
await wino.Dispatcher.InvokeAsync(async () =>
{
    wino.Get<IMessenger>().Send(new ActiveMailFolderChangedEvent(parameter.BaseFolderMenuItem, parameter.FolderInitLoadAwaitTask));
    await folderLoad.Task;
    await list.WaitForCurrentFolderInitializationAsync();           // the list is loaded now
});

wino.AnswerNextDialog(nameof(IMailDialogService.ShowHardDeleteConfirmationAsync), true);   // the script is the user
await wino.Dispatcher.InvokeAsync(() => list.ExecuteMailOperationAsync(
    new MailOperationPreperationRequest(MailOperation.HardDelete, list.MailCollection.Items[0].MailCopy)));
await wino.IdleAsync();                                               // nothing queued, nothing running, dispatcher empty
```

- `AddImapAccountAsync` builds the plan the setup page builds, runs `IAccountSetupRunner` on the dispatcher, and sends `AccountCreatedMessage` as the setup page does. `LabAccountModes` chooses what the account gets: `MailOnly` (the default), `AllThroughDav`, `AllLocal`, or single switches for calendar, contacts and tasks. `lease.Account("pop3")` gives a POP3 account.
- `OpenAsync` resolves the ViewModel, assigns its `Dispatcher`, and calls `OnNavigatedTo` and `OnPageLoaded`. The mail list loads a folder only after `ActiveMailFolderChangedEvent`, which the shell sends in the app.
- Read ViewModel state and invoke commands inside `wino.Dispatcher.InvokeAsync(...)`.
- A dialog resolves as dismissed unless the script queued an answer with `AnswerNextDialog<T>(dialogName, answer)`. The type must match what the dialog returns: `bool` for a confirmation, `string` for text input, `IMailItemFolder` for the folder picker. Each answer is used once.
- `IdleAsync` counts work started by a sync request, not the runtime's periodic loops.
- The host's app lock is PIN-only and off. A script turns it on through `IAppLockService`, and answers its prompt with `AnswerNextDialog("PromptVerificationAsync", "<pin>")`; the real lock service checks the PIN.
- To show the write rule, read the item in the ViewModel right after the command, the database row at that moment, then both the database and the server after `IdleAsync`. Read the server through your own connection, not through the app.
- Exchange and the providers that sign in through a browser have no helper: build the `AccountSetupPlan` and run it through `IAccountSetupRunner` inside `wino.Dispatcher.InvokeAsync(...)`.

Never print or store the lab password or the control token.

## Performance

- The first build of a script compiles the whole backend and takes about a minute; later runs take seconds. Keep one script folder per task so its build output is reused.
- `DisposeAsync` stops the runtime, closes the database pool and the log, and stops the dispatcher. Always dispose the host, or the data root stays locked.
