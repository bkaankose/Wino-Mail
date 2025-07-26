using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces;

public interface INotificationBuilder
{
    /// <summary>
    /// Creates toast notifications for new mails.
    /// </summary>
    Task CreateNotificationsAsync(Guid inboxFolderId, IEnumerable<IMailItem> newMailItems);

    /// <summary>
    /// Gets the unread Inbox messages for each account and updates the taskbar icon.
    /// </summary>
    /// <returns></returns>
    Task UpdateTaskbarIconBadgeAsync();

    /// <summary>
    /// Creates test notification for test purposes.
    /// </summary>
    Task CreateTestNotificationAsync(string title, string message);

    /// <summary>
    /// Removes the toast notification for a specific mail by unique id.
    /// </summary>
    void RemoveNotification(Guid mailUniqueId);
}
