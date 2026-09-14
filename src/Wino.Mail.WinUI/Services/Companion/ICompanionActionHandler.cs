using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Services.Companion;

internal interface ICompanionActionHandler
{
    Task OpenWinoAsync(CancellationToken cancellationToken);
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
