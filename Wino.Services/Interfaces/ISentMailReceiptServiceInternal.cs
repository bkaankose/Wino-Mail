using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Companion-process-only surface of <see cref="ISentMailReceiptService"/> that works on
/// parsed MimeMessages (read-receipt tracking and processing).
/// </summary>
public interface ISentMailReceiptServiceInternal : ISentMailReceiptService
{
    Task TrackSentMailAsync(MailCopy mailCopy, MimeMessage mimeMessage = null);
    Task ProcessIncomingReceiptAsync(MailCopy receiptMail, MimeMessage mimeMessage);
}
