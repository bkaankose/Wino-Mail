using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Platform.Windows.Services;

public interface INotificationHostClient
{
    Task ShowAsync(
        NotificationHostApplication application,
        AppNotification notification,
        CancellationToken cancellationToken = default);

    Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>Hands a toast to Windows to show at <paramref name="deliveryTime"/>, also while Wino is closed.</summary>
    void Schedule(NotificationHostApplication application, AppNotification notification, DateTimeOffset deliveryTime);

    /// <summary>The tag and delivery time of every toast Wino scheduled for the application that has not been shown yet.</summary>
    IReadOnlyList<(string Tag, DateTimeOffset DeliveryTime)> GetScheduled(NotificationHostApplication application);

    /// <summary>True when a toast with <paramref name="tag"/> is in the application's notification center.</summary>
    bool IsInHistory(NotificationHostApplication application, string tag);

    /// <summary>Removes the scheduled toasts with <paramref name="tag"/> that have not been shown yet.</summary>
    void RemoveScheduled(NotificationHostApplication application, string tag);
}
