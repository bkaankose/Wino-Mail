using AppKit;
using Foundation;
using UserNotifications;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>Native notifications and Dock badges. Actions/reminder scheduling follow in the parity work.</summary>
public sealed class MacNotificationBuilder(IDispatcher dispatcher, IPreferencesService preferences) : INotificationBuilder
{
    private int _calendarCount;
    private Task? _authorization;

    private Task AuthorizeAsync() => _authorization ??= RequestAuthorizationAsync();

    private static Task RequestAuthorizationAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
            (granted, error) =>
            {
                if (error != null || !granted) completion.TrySetException(new InvalidOperationException("Notification permission was not granted."));
                else completion.TrySetResult();
            });
        return completion.Task;
    }

    private async Task NotifyAsync(string title, string body, string? identifier = null)
    {
        await AuthorizeAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            using var content = new UNMutableNotificationContent { Title = title, Body = body };
            using var request = UNNotificationRequest.FromIdentifier(identifier ?? Guid.NewGuid().ToString("N"), content, null);
            UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
            {
                if (error != null) completion.TrySetException(new InvalidOperationException("macOS could not deliver the notification."));
                else completion.TrySetResult();
            });
        });
        await completion.Task;
    }

    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
        => preferences.AreNewMailNotificationsEnabled ? CreateTestNotificationsAsync(newMailItems) : Task.CompletedTask;
    public async Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems)
    {
        foreach (var message in mailItems)
            await NotifyAsync(string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : message.FromName,
                message.Subject ?? string.Empty, message.UniqueId.ToString("N"));
    }
    public Task UpdateTaskbarIconBadgeAsync() => dispatcher.ExecuteOnUIThread(() => NSApplication.SharedApplication.DockTile.Display());
    public Task UpdateJumpListOptionsAsync() => Task.FromException(new PlatformNotSupportedException("Windows Jump Lists have no Mac implementation; the Dock menu belongs to the application head."));
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount)
    {
        _calendarCount += newlyDownloadedCount;
        return dispatcher.ExecuteOnUIThread(() => NSApplication.SharedApplication.DockTile.BadgeLabel = _calendarCount > 0 ? _calendarCount.ToString() : string.Empty);
    }
    public Task ClearCalendarTaskbarBadgeAsync()
    {
        _calendarCount = 0;
        return dispatcher.ExecuteOnUIThread(() => NSApplication.SharedApplication.DockTile.BadgeLabel = string.Empty);
    }
    public void RemoveNotification(Guid mailUniqueId)
    {
        var identifier = mailUniqueId.ToString("N");
        UNUserNotificationCenter.Current.RemoveDeliveredNotifications([identifier]);
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests([identifier]);
    }
    public void CreateAttentionRequiredNotification(MailAccount account) => Observe(NotifyAsync("Wino Mail", "An account requires attention."));
    public void CreateWebView2RuntimeMissingNotification() => Observe(NotifyAsync("Wino Mail", "The native reader runtime is unavailable."));
    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
        => Task.FromException(new PlatformNotSupportedException("Calendar reminder actions and snoozing follow the OAuth/sidebar foundation."));
    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => CreateCalendarReminderNotificationAsync(calendarItem, 0);
    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => NotifyAsync("Wino Mail", "Contact notification");
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => NotifyAsync("Wino Mail", "Task reminder");

    private static void Observe(Task task) => _ = task.ContinueWith(t => Serilog.Log.Error(t.Exception, "Native notification failed."), TaskContinuationOptions.OnlyOnFaulted);
}
