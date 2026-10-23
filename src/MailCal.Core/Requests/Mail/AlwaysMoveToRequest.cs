using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Mail;

public record AlwaysMoveToRequest(MailCopy Item, bool MoveToFocused) : MailRequestBase(Item)
{
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.AlwaysMoveTo;
}

public class BatchAlwaysMoveToRequest : BatchCollection<AlwaysMoveToRequest>
{
    public BatchAlwaysMoveToRequest(IEnumerable<AlwaysMoveToRequest> collection) : base(collection)
    {
    }
}
