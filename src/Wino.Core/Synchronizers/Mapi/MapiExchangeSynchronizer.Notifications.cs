using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Rops;
using Wino.Core.MAPI.Wire;

namespace Wino.Core.Synchronizers.Mapi;

public sealed partial class MapiExchangeSynchronizer
{
    protected override string TransportName => "MAPI notifications";

    /// <summary>
    /// A dedicated session (the shared one serves sync passes and must not sit in a long poll) subscribes to the whole
    /// store, then loops on NotificationWait. Each wake-up collects the pending RopNotify responses with an empty
    /// Execute. Returns only through cancellation or a failure; the server keeps the session alive while it is polled.
    /// </summary>
    protected override async Task ListenForServerChangesAsync(ServerChangeSink sink, CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);

        // The subscription's handle lands in slot 1 and must stay alive for the session's lifetime.
        var (rops, _) = await session.ExecuteAsync(
            RopNotify.BuildRegisterNotification(0, 1, RopNotify.NotificationTypes.StoreChanges),
            [session.LogonHandle, RopExecute.NullHandle], cancellationToken).ConfigureAwait(false);
        RopNotify.ParseRegisterNotification(new RopReader(rops));

        sink.Listening();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await session.NotificationWaitAsync(cancellationToken).ConfigureAwait(false))
                continue;

            var (notifyRops, _) = await session.ExecuteAsync([], [session.LogonHandle], cancellationToken).ConfigureAwait(false);

            List<RopNotify.Notification> notifications;
            try
            {
                notifications = RopNotify.ParseNotifications(notifyRops);
            }
            catch (MapiFormatException ex)
            {
                Logger.Warning(ex, "MAPI notify {Account}: notification buffer not fully decoded ({Bytes} bytes); synchronizing everything.", Account.Address, notifyRops.Length);
                sink.Unattributed();
                continue;
            }

            foreach (var notification in notifications)
            {
                if (notification.IsTable || notification.FolderId is not { } folderId)
                    continue;

                // Folder Modified is a count or property change (every arrival raises one) and is covered by the message
                // events; only structural folder events need the hierarchy.
                if (!notification.IsMessage)
                {
                    if (notification.Type is RopNotify.NotificationTypes.ObjectCreated or RopNotify.NotificationTypes.ObjectDeleted
                                          or RopNotify.NotificationTypes.ObjectMoved or RopNotify.NotificationTypes.ObjectCopied)
                        sink.HierarchyChanged();

                    continue;
                }

                sink.FolderItemsChanged(ToMapiId(folderId));

                // A moved message also left its old folder.
                if (notification.OldFolderId is { } oldFolderId && oldFolderId != folderId)
                    sink.FolderItemsChanged(ToMapiId(oldFolderId));
            }

            if (notifications.Count > 0)
            {
                Logger.Debug("MAPI notify {Account}: {Count} notification(s) ({Types}).",
                    Account.Address, notifications.Count, string.Join(",", notifications.Select(n => n.Type).Distinct()));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    protected override bool IsPermanentListenerFailure(Exception exception, out string reason)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case MapiNotAdvertisedException:
                    reason = "server offers no MAPI over HTTP";
                    return true;
                case MapiTransportException { IsUnauthorized: true }:
                    reason = "server rejected the credentials";
                    return true;
            }
        }

        return base.IsPermanentListenerFailure(exception, out reason);
    }
}
