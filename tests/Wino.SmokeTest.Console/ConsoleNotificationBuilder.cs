using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.SmokeTest.ConsoleApp;

internal sealed class ConsoleNotificationBuilder : INotificationBuilder
{
    public Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems) => Task.CompletedTask;
    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => Task.CompletedTask;
    public Task UpdateTaskbarIconBadgeAsync() => Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;
    public void RemoveNotification(Guid mailUniqueId) { }
    public void CreateAttentionRequiredNotification(MailAccount account) { }
    public void CreateWebView2RuntimeMissingNotification() { }
    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds) => Task.CompletedTask;
    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Task.CompletedTask;
    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Task.CompletedTask;
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Task.CompletedTask;
}
