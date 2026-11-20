using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Notifications;

/// <summary>
/// Desktop notifications through <c>GNotification</c>, which GLib delivers over org.freedesktop.Notifications (or the
/// notification portal inside a sandbox). The shell shows them under the app's name only when the installed
/// <c>.desktop</c> file is named after the application id. Proof of concept: new mail only.
/// </summary>
public sealed class LinuxNotificationBuilder(IDispatcher dispatcher) : INotificationBuilder
{
    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        var items = newMailItems?.ToList() ?? [];
        return dispatcher.ExecuteOnUIThread(() =>
        {
            if (Gio.Application.GetDefault() is not { } application) return;
            foreach (var mail in items)
            {
                var notification = Gio.Notification.New(string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName);
                notification.SetBody(mail.Subject ?? string.Empty);
                notification.SetIcon(Gio.ThemedIcon.New("mail-unread-symbolic"));
                application.SendNotification(mail.UniqueId.ToString("N"), notification);
            }
        });
    }

    public void RemoveNotification(Guid mailUniqueId)
        => dispatcher.ExecuteOnUIThread(() => Gio.Application.GetDefault()?.WithdrawNotification(mailUniqueId.ToString("N")));

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => CreateNotificationsAsync(mailItems);

    public Task UpdateTaskbarIconBadgeAsync() => Task.CompletedTask;

    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;

    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;

    public void CreateAttentionRequiredNotification(MailAccount account) { }

    public void CreateWebView2RuntimeMissingNotification() { }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds) => Task.CompletedTask;

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Task.CompletedTask;

    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Task.CompletedTask;

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Task.CompletedTask;

    public Task CreateLiveNotificationsTestNotificationAsync() => Task.CompletedTask;
}
