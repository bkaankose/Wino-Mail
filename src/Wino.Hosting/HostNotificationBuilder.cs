using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Hosting;

/// <summary>Notifications for a host with no notification center: each one is recorded with what it would show.</summary>
internal sealed class HostNotificationBuilder(HostInteractionLog log) : INotificationBuilder
{
    private Task Record(string name, string? detail = null)
    {
        log.Record(HostInteractionKind.Notification, name, detail);
        return Task.CompletedTask;
    }

    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        var items = newMailItems?.ToList() ?? [];
        return Record(nameof(CreateNotificationsAsync), $"{items.Count} new: " + string.Join(" | ", items.Take(5).Select(item => item.Subject)));
    }

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => Record(nameof(CreateTestNotificationsAsync));

    public Task UpdateTaskbarIconBadgeAsync() => Record(nameof(UpdateTaskbarIconBadgeAsync));

    public Task UpdateJumpListOptionsAsync() => Record(nameof(UpdateJumpListOptionsAsync));

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Record(nameof(AddCalendarTaskbarBadgeCountAsync), newlyDownloadedCount.ToString());

    public Task ClearCalendarTaskbarBadgeAsync() => Record(nameof(ClearCalendarTaskbarBadgeAsync));

    public void RemoveNotification(Guid mailUniqueId) => Record(nameof(RemoveNotification), mailUniqueId.ToString());

    public void CreateAttentionRequiredNotification(MailAccount account) => Record(nameof(CreateAttentionRequiredNotification), account?.Address);

    public void CreateReaderRuntimeMissingNotification() => Record(nameof(CreateReaderRuntimeMissingNotification));

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
        => Record(nameof(CreateCalendarReminderNotificationAsync), calendarItem?.Title);

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Record(nameof(CreateTestCalendarReminderNotificationAsync));

    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Record(nameof(CreateTestPeopleNotificationAsync));

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Record(nameof(CreateTestTaskReminderNotificationAsync));

    public Task CreateTaskReminderNotificationAsync(AccountTask task) => Record(nameof(CreateTaskReminderNotificationAsync), task?.Title);

    public Task CreateLiveNotificationsTestNotificationAsync() => Record(nameof(CreateLiveNotificationsTestNotificationAsync));
}
