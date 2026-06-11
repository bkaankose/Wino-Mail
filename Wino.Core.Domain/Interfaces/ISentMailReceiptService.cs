using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

[Wino.Core.Domain.Attributes.WinoRpcService]
public interface ISentMailReceiptService
{
    Task PopulateReceiptStateAsync(MailCopy mailCopy);
    Task PopulateReceiptStatesAsync(IReadOnlyCollection<MailCopy> mailCopies);
}
