using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Core.Domain.Interfaces;

public interface ISentMailReceiptService
{
    Task PopulateReceiptStateAsync(MailCopy mailCopy);
    Task PopulateReceiptStatesAsync(IReadOnlyCollection<MailCopy> mailCopies);
    Task TrackSentMailAsync(MailCopy mailCopy, MimeMessage mimeMessage = null);
    Task ProcessIncomingReceiptAsync(MailCopy receiptMail, MimeMessage mimeMessage);
}
