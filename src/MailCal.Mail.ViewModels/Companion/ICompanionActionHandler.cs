#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Enums;
using MailCal.Mail.ViewModels.Data;

namespace MailCal.Mail.ViewModels.Companion;

public interface ICompanionActionHandler
{
    Task OpenMailCalAsync(CancellationToken cancellationToken);
    Task OpenCalendarAsync(CancellationToken cancellationToken);
    Task OpenTasksAsync(CancellationToken cancellationToken);
    Task OpenInboxAsync(Guid? accountId, CancellationToken cancellationToken);
    Task OpenMailAsync(MailItemViewModel mail, CancellationToken cancellationToken);
    Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken);
    Task JoinCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken);
    Task FindContactAsync(AccountContactViewModel? contact, CancellationToken cancellationToken);
    Task NewMailAsync(CancellationToken cancellationToken);
    Task NewEventAsync(CancellationToken cancellationToken);
    Task OpenSettingsAsync(CancellationToken cancellationToken);
    Task StartNotificationSnoozeAsync(NotificationSnoozePreset preset, CancellationToken cancellationToken);
    Task ResumeNotificationsAsync(CancellationToken cancellationToken);
    Task SetMailReadAsync(MailItemViewModel mail, bool isRead, CancellationToken cancellationToken);
    Task ArchiveMailAsync(MailItemViewModel mail, CancellationToken cancellationToken);
    Task SetTaskCompletedAsync(TaskItemViewModel task, bool isCompleted, CancellationToken cancellationToken);
    Task CreateMyDayTaskAsync(string title, CancellationToken cancellationToken);
}
