using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// Removes the toast notification for a specific mail by unique id.
    /// </summary>
    void RemoveNotification(Guid mailUniqueId);

    /// <summary>
    /// Shows a notification that the account requires attention.
    /// </summary>
    /// <param name="account">Account that needs attention.</param>
    void CreateAttentionRequiredNotification(MailAccount account);
}
