using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using MailCal.NotificationHost.Contracts;

namespace MailCal.Mail.WinUI.Services;

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
}
