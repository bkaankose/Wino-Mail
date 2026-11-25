using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Services.Interfaces;

/// <summary>
/// Moves mail between accounts: the target receives an exact copy first, and the source moves its mail to Trash only
/// after the target confirmed the copy. A failure leaves the mail in both accounts, never in neither.
/// </summary>
public interface IMailTransferService
{
    /// <summary>
    /// Throws <c>InvalidMoveTargetException</c>, <c>UnavailableSpecialFolderException</c> or
    /// <c>MailOperationDecisionRequiredException</c> when the move cannot start; nothing has changed then.
    /// </summary>
    Task ValidateMoveAsync(IReadOnlyCollection<MailCopy> mailItems, IMailItemFolder targetFolder);

    /// <summary>Validates, hides the mail from its folder and moves it in the background one message at a time.</summary>
    Task StartMoveAsync(IReadOnlyCollection<MailCopy> mailItems, IMailItemFolder targetFolder);

    /// <summary>Mail that is being moved and has not reached its account's request queue yet.</summary>
    IReadOnlyCollection<Guid> GetPendingUniqueIds();

    /// <summary>
    /// The account's own folders followed by one container per other account that can receive moved mail, holding
    /// that account's folders. <paramref name="displayShape"/> shapes each tree like the navigation menu.
    /// </summary>
    Task<List<IMailItemFolder>> GetMoveTargetsAsync(Guid sourceAccountId, bool displayShape);

    /// <summary>
    /// Whether a drop of mail from another account onto this account may be offered. False only when the account is
    /// known not to receive moved mail; the move itself checks again.
    /// </summary>
    bool MayReceiveMovedMail(Guid accountId);
}
