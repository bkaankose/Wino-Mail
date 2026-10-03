using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Shows and removes toasts under the per-mode notification identities without leaving this process.
/// Windows lets a packaged app address any application in its own package, so the notification host
/// executables are only started by Windows when a toast is activated.
/// </summary>
internal sealed class NotificationHostClient : INotificationHostClient
{
    // ToastNotificationHistory can only address another application's toast by tag and group together.
    private const string DefaultGroup = "wino";

    public Task ShowAsync(
        NotificationHostApplication application,
        AppNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new XmlDocument();
        document.LoadXml(notification.Payload);

        var toast = new ToastNotification(document)
        {
            Group = string.IsNullOrWhiteSpace(notification.Group) ? DefaultGroup : notification.Group
        };

        if (!string.IsNullOrWhiteSpace(notification.Tag))
            toast.Tag = notification.Tag;

        ToastNotificationManager.CreateToastNotifier(GetAppUserModelId(application)).Show(toast);
        return Task.CompletedTask;
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        cancellationToken.ThrowIfCancellationRequested();

        ToastNotificationManager.History.Remove(tag, DefaultGroup, GetAppUserModelId(application));
        return Task.CompletedTask;
    }

    private static string GetAppUserModelId(NotificationHostApplication application)
        => $"{Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.GetApplicationId(application)}";
}
