#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The UI side of <see cref="INotificationResponseService"/>: shows the shell, a mail in its folder,
/// the Drafts folder and the composer. Implemented in the ViewModels layer over the head's shell host.
/// </summary>
public interface INotificationResponseNavigator
{
    /// <summary>Shows the shell in <paramref name="mode"/> with an optional mode parameter.</summary>
    Task OpenModeAsync(WinoApplicationMode mode, object? parameter = null);

    /// <summary>Shows mail mode with <paramref name="mailItem"/> selected in folder <paramref name="folderId"/>.</summary>
    Task OpenMailAsync(Guid accountId, Guid folderId, MailCopy mailItem);

    /// <summary>Shows mail mode on the account's Drafts folder so the reading pane can host the composer. False when the shell could not be shown.</summary>
    Task<bool> ShowDraftsAsync(Guid accountId);

    /// <summary>Opens <paramref name="draft"/> in the composer with focus.</summary>
    Task OpenComposerAsync(MailCopy draft);
}
