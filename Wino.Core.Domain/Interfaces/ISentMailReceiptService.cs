using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

[Wino.Core.Domain.Attributes.WinoRpcService]
public interface ISentMailReceiptService
{
    Task PopulateReceiptStateAsync(MailCopy mailCopy);
    Task PopulateReceiptStatesAsync(IReadOnlyCollection<MailCopy> mailCopies);
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task TrackSentMailAsync(MailCopy mailCopy, MimeMessage mimeMessage = null);
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task ProcessIncomingReceiptAsync(MailCopy receiptMail, MimeMessage mimeMessage);
}
