#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Reads iMIP invitations (RFC 6047) from mail and answers them. Works from the message MIME, so
/// mail synced before invitations were recognized needs no resynchronization.
/// </summary>
public interface IMailInvitationService
{
    /// <summary>Returns null when the message has no text/calendar part with a VEVENT.</summary>
    MailInvitation? Parse(MimeMessage message);

    /// <summary>
    /// Returns null when the message is not an invitation. Also corrects the stored
    /// <see cref="MailCopy.ItemType"/> when it does not match the calendar part.
    /// </summary>
    Task<MailInvitationState?> GetStateAsync(MailCopy mailCopy, MimeMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the invitation as the mail's account. Changes local data only after the server
    /// confirmed the change; throws with a user-facing message on failure.
    /// </summary>
    Task<MailInvitationState> RespondAsync(MailCopy mailCopy, MimeMessage message, AttendeeStatus response, string? comment, CancellationToken cancellationToken = default);

    /// <summary>Removes the event a cancellation refers to from the account's calendar.</summary>
    Task<MailInvitationState> RemoveCancelledEventAsync(MailCopy mailCopy, MimeMessage message, CancellationToken cancellationToken = default);
}
