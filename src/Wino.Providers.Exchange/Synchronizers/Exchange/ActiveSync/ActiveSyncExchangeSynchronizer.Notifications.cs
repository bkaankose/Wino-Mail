using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// The ActiveSync change listener: a Ping long poll over the synchronized collections that turns a changed collection
/// into a folder-scoped synchronization request, as EWS streaming and MAPI notifications do for the other transports.
/// It runs on its own HTTP client and never takes the engine's synchronization gate.
/// </summary>
internal sealed partial class ActiveSyncExchangeSynchronizer : IExchangeSynchronizer, ILiveChangeListener
{
    private static readonly XNamespace PingNamespace = "Ping:";
    private static readonly TimeSpan[] ListenerReconnectDelays =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    // Exchange allows 60 to 3540 seconds by default and names its limit in status 5. Fifteen minutes stays under the
    // idle timeout of most proxies and NAT devices; a Ping that dies silently halves it.
    private const int InitialHeartbeatSeconds = 900;
    private const int MinimumHeartbeatSeconds = 120;
    private static readonly TimeSpan PingAnswerGrace = TimeSpan.FromSeconds(60);

    // A Ping answered with errors (bad request, policy, credentials) comes back at once; one still open after this
    // long has been accepted by the server.
    private static readonly TimeSpan PingAcceptedAfter = TimeSpan.FromSeconds(3);

    // Ping reports a collection as changed until a Sync reads the changes, so the next Ping waits for that pass.
    private static readonly TimeSpan SynchronizationWaitLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NoCollectionsRetry = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _listenerLifecycle = new(1, 1);
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private ExchangeNotificationStatus _notificationStatus = ExchangeNotificationStatus.NeverStarted;
    private volatile bool _notificationStatusClosed;
    private int _heartbeatSeconds = InitialHeartbeatSeconds;
    private int? _maximumPingFolders;

    Task ILiveChangeListener.StartListeningAsync() => StartChangeListenerAsync();
    Task ILiveChangeListener.SuspendListeningAsync() => StopChangeListenerAsync();

    public ExchangeNotificationStatus NotificationStatus => Volatile.Read(ref _notificationStatus);

    public async Task StartChangeListenerAsync()
    {
        if (Account.ServerInformation == null || Account.IsNetworkAccessBlocked())
            return;

        await _listenerLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDisposing || _listenerTask is { IsCompleted: false })
                return;

            _listenerCancellation?.Dispose();
            _listenerCancellation = new CancellationTokenSource();
            _listenerTask = RunChangeListenerAsync(_listenerCancellation.Token);
        }
        finally
        {
            _listenerLifecycle.Release();
        }
    }

    public async Task StopChangeListenerAsync()
    {
        Task? running;
        await _listenerLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _listenerCancellation?.Cancel();
            running = _listenerTask;
            _listenerTask = null;
        }
        finally
        {
            _listenerLifecycle.Release();
        }

        if (running != null)
        {
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ActiveSync change listener of {AccountName} did not stop cleanly.", Account.Name);
            }
        }
    }

    private async Task RunChangeListenerAsync(CancellationToken cancellationToken)
    {
        // Yield first so synchronizer creation is never held up by the first request.
        await Task.Yield();

        var failures = 0;
        var exitReason = "stopped";
        Exception? exitException = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsDisposing)
            {
                if (Account.IsNetworkAccessBlocked())
                {
                    exitReason = "account needs attention";
                    return;
                }

                try
                {
                    var folders = await _engine.GetPingFoldersAsync().ConfigureAwait(false);
                    if (folders.Count == 0)
                    {
                        SetNotificationStatus(ExchangeNotificationState.Reconnecting, "waiting for the first ActiveSync synchronization");
                        await Task.Delay(NoCollectionsRetry, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var watched = Prioritize(folders);
                    if (_maximumPingFolders is { } maximum && watched.Count > maximum)
                    {
                        Logger.Warning("ActiveSync Ping for {AccountName} watches {Maximum} of {Count} collections; the server allows no more.",
                            Account.Name, maximum, watched.Count);
                        watched = watched.Take(maximum).ToList();
                    }

                    var response = await PingOnceAsync(watched, () =>
                    {
                        failures = 0;
                        SetNotificationStatus(ExchangeNotificationState.Listening, "ActiveSync Ping");
                    }, cancellationToken).ConfigureAwait(false);

                    await HandlePingResponseAsync(response, watched, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsPermanentListenerFailure(ex, out var reason))
                {
                    exitReason = reason;
                    exitException = ex;
                    return;
                }
                catch (Exception ex)
                {
                    var delay = ListenerReconnectDelays[Math.Min(failures, ListenerReconnectDelays.Length - 1)];
                    failures++;

                    if (ex is EasServerBusyException { RetryAfter: { } retryAfter } && retryAfter > delay)
                        delay = retryAfter;

                    if (ex is TimeoutException && _heartbeatSeconds > MinimumHeartbeatSeconds)
                        _heartbeatSeconds = Math.Max(MinimumHeartbeatSeconds, _heartbeatSeconds / 2);

                    SetNotificationStatus(ExchangeNotificationState.Reconnecting, $"ActiveSync Ping failed, retrying in {delay.TotalSeconds:F0} s", ex);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            exitReason = "listener failed";
            exitException = ex;
        }
        finally
        {
            SetNotificationStatus(ExchangeNotificationState.Stopped, exitReason, exitException);
        }
    }

    /// <summary>Sends one Ping and reports the channel as listening once the server has held it open or answered normally.</summary>
    private async Task<XElement> PingOnceAsync(IReadOnlyList<EasPingFolder> watched, Action listening, CancellationToken cancellationToken)
    {
        var heartbeat = _heartbeatSeconds;
        var request = new XElement(PingNamespace + "Ping",
            new XElement(PingNamespace + "HeartbeatInterval", heartbeat.ToString(CultureInfo.InvariantCulture)),
            new XElement(PingNamespace + "Folders", watched.Select(folder => new XElement(PingNamespace + "Folder",
                new XElement(PingNamespace + "Id", folder.ServerId),
                new XElement(PingNamespace + "Class", folder.Class)))));

        var ping = _engine.PingAsync(request, TimeSpan.FromSeconds(heartbeat) + PingAnswerGrace, cancellationToken);
        if (await Task.WhenAny(ping, Task.Delay(PingAcceptedAfter, cancellationToken)).ConfigureAwait(false) != ping)
            listening();

        var response = await ping.ConfigureAwait(false);
        if (EasStatus(response) is "1" or "2")
            listening();

        return response;
    }

    private async Task HandlePingResponseAsync(XElement response, IReadOnlyList<EasPingFolder> watched, CancellationToken cancellationToken)
    {
        var status = EasStatus(response);
        switch (status)
        {
            case "1":
                return;

            case "2":
                var changedIds = response.Element(PingNamespace + "Folders")?.Elements(PingNamespace + "Folder")
                    .Select(element => element.Value)
                    .ToHashSet(StringComparer.Ordinal) ?? [];
                var changed = watched.Where(folder => changedIds.Contains(folder.ServerId)).ToList();
                if (changed.Count == 0)
                    return;

                var reportedUtc = DateTime.UtcNow;
                RequestSynchronization(changed);
                await WaitForAsync(() => changed.All(folder => _engine.LastCollectionPassUtc(folder.ServerId) > reportedUtc), cancellationToken).ConfigureAwait(false);
                return;

            case "5":
                var limit = response.Element(PingNamespace + "HeartbeatInterval")?.Value;
                if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds == _heartbeatSeconds)
                    throw new EasCommandException("Ping", "5");

                Logger.Information("ActiveSync Ping for {AccountName}: the server allows a heartbeat of {Seconds} s instead of {Previous} s.",
                    Account.Name, seconds, _heartbeatSeconds);
                _heartbeatSeconds = seconds;
                return;

            case "6":
                var maximumText = response.Element(PingNamespace + "MaxFolders")?.Value;
                if (!int.TryParse(maximumText, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) || maximum <= 0 || maximum >= watched.Count)
                    throw new EasCommandException("Ping", "6");

                _maximumPingFolders = maximum;
                return;

            case "7":
                var requestedUtc = DateTime.UtcNow;
                Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions { AccountId = Account.Id, Type = MailSynchronizationType.FoldersOnly }));
                await WaitForAsync(() => _engine.LastHierarchyPassUtc > requestedUtc, cancellationToken).ConfigureAwait(false);
                return;

            default:
                // 3 (parameters missing), 4 (protocol error) and 8 (server error) are retried with the backoff.
                throw new EasCommandException("Ping", status ?? "missing");
        }
    }

    /// <summary>
    /// Waits until the synchronization the listener asked for has run, so the next Ping does not report the same change
    /// again at once. A request that never runs (the account is busy, or the change was in a folder no pass covers) ends
    /// the wait after a limit.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SynchronizationWaitLimit;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    private void RequestSynchronization(IReadOnlyList<EasPingFolder> changed)
    {
        var mailFolderIds = changed.Where(folder => folder.Class == "Email" && folder.EntityId.HasValue).Select(folder => folder.EntityId!.Value).ToList();

        Logger.Debug("ActiveSync Ping for {AccountName}: {Count} collection(s) changed ({Classes}).",
            Account.Name, changed.Count, string.Join(",", changed.Select(folder => folder.Class).Distinct()));

        if (mailFolderIds.Count > 0)
        {
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.CustomFolders,
                SynchronizationFolderIds = mailFolderIds,

                // The server named the folders that changed; Inbox, Sent, Drafts and Deleted are not added on top.
                ExcludeMustHaveFolders = true
            }));
        }

        if (changed.Any(folder => folder.Class == "Calendar"))
            Messenger.Send(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions { AccountId = Account.Id, Type = CalendarSynchronizationType.CalendarEvents }));

        if (changed.Any(folder => folder.Class == "Contacts"))
            Messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions { AccountId = Account.Id, Type = ContactSynchronizationType.Full }));

        if (changed.Any(folder => folder.Class == "Tasks"))
            Messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions { AccountId = Account.Id, Type = TaskSynchronizationType.Full }));
    }

    /// <summary>The Inbox first, then calendars, the other server folders, contacts, tasks and the remaining mail folders, for a server that caps the count.</summary>
    private static List<EasPingFolder> Prioritize(IEnumerable<EasPingFolder> folders)
        => folders.OrderBy(folder => folder.Type switch
        {
            2 => 0,
            8 or 13 => 1,
            3 or 4 or 5 => 2,
            9 or 14 => 3,
            7 or 15 => 4,
            _ => 5
        }).ToList();

    private static string? EasStatus(XElement response)
        => response.Elements().FirstOrDefault(element => element.Name.LocalName == "Status")?.Value;

    /// <summary>Failures a reconnect cannot fix; the synchronization path surfaces them to the user.</summary>
    private static bool IsPermanentListenerFailure(Exception exception, out string reason)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationAttentionException:
                    reason = "sign-in needs attention";
                    return true;
                case EasRemoteWipeException:
                    reason = "the server asked to wipe this device";
                    return true;
                case EasPolicyException:
                    reason = "the device policy cannot be met";
                    return true;
                case EasAccessDeniedException:
                    reason = "the server refuses this device";
                    return true;
                case NotSupportedException:
                    reason = "the server offers no usable ActiveSync version";
                    return true;
                case HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }:
                    reason = "server rejected the credentials";
                    return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private void SetNotificationStatus(ExchangeNotificationState state, string reason, Exception? exception = null)
    {
        if (_notificationStatusClosed)
            return;

        var previous = Volatile.Read(ref _notificationStatus);
        var status = new ExchangeNotificationStatus(state, reason, DateTimeOffset.UtcNow);
        Volatile.Write(ref _notificationStatus, status);

        if (previous.State == state && previous.Reason == reason)
            return;

        if (exception == null)
            Logger.Information("ActiveSync change listener for {AccountName} is {State} ({Reason}).", Account.Name, state, reason);
        else
            Logger.Information(exception, "ActiveSync change listener for {AccountName} is {State} ({Reason}).", Account.Name, state, reason);

        try
        {
            Messenger.Send(new ExchangeNotificationStateChanged(Account.Id, status));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not publish the ActiveSync listener state of {AccountName}.", Account.Name);
        }
    }

    private async Task CloseChangeListenerAsync()
    {
        // A replacement synchronizer (a transport switch) can start listening before this Ping returns; say "stopped"
        // now and nothing after it, so the late exit never overwrites the replacement's state in the coordinator.
        SetNotificationStatus(ExchangeNotificationState.Stopped, "synchronizer closed");
        _notificationStatusClosed = true;
        await StopChangeListenerAsync().ConfigureAwait(false);
    }
}
