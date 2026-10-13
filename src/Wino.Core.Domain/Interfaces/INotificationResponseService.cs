#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Applies the business rules for a notification response: calendar Open, Join and Snooze, mail Open,
/// mail actions, Reply, Reply all and Forward drafts, inline reply and mode routing. Heads translate
/// their native response to <see cref="NotificationResponse"/> and call this once services are running.
/// </summary>
public interface INotificationResponseService
{
    Task HandleAsync(NotificationResponse response);

    /// <summary>Shows a mail in its folder; mail mode when the mail no longer exists.</summary>
    Task OpenMailAsync(Guid mailUniqueId);

    /// <summary>Shows a calendar item in the calendar; the calendar when the item no longer exists.</summary>
    Task OpenCalendarItemAsync(Guid calendarItemId);

    /// <summary>Opens the online meeting link of a calendar item, when it has one.</summary>
    Task JoinOnlineAsync(Guid calendarItemId);
}
