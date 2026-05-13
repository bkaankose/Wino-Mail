using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface INotificationBuilder
{
    /// <summary>
    /// Creates toast notifications for new mails.
    /// </summary>
    Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems);

    /// <summary>
    /// Gets the unread Inbox messages for each account and updates the taskbar icon.
    /// </summary>
    /// <returns></returns>
    Task UpdateTaskbarIconBadgeAsync();

    /// <summary>
    /// Rebuilds the taskbar jump list entries for accounts and folders that opt in.
    /// </summary>
    Task UpdateJumpListOptionsAsync();

    /// <summary>
    /// Adds to the calendar app-entry badge count for newly downloaded events.
    /// </summary>
    Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount);

    /// <summary>
    /// Clears the calendar app-entry badge.
    /// </summary>
    Task ClearCalendarTaskbarBadgeAsync();

    /// <summary>
    /// Removes the toast notification for a specific mail by unique id.
    /// </summary>
    void RemoveNotification(Guid mailUniqueId);

    /// <summary>
    /// Shows a notification that the account requires attention.
    /// </summary>
    /// <param name="account">Account that needs attention.</param>
    void CreateAttentionRequiredNotification(MailAccount account);

    /// <summary>
    /// Shows a notification when WebView2 runtime is unavailable.
    /// </summary>
    void CreateWebView2RuntimeMissingNotification();

    /// <summary>
    /// Shows a notification when a Microsoft Store update is available.
    /// </summary>
    void CreateStoreUpdateNotification();

    /// <summary>
    /// Shows the one-time release migration notification.
    /// </summary>
    void CreateReleaseMigrationNotification();

    /// <summary>
    /// Creates a calendar reminder toast for the specified calendar item.
    /// </summary>
    Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds);
}

