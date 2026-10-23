using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Requests;

namespace MailCal.Core.Requests.Mail;

public record MoveToFocusedRequest(MailCopy Item, bool MoveToFocused) : MailRequestBase(Item)
{
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.MoveToFocused;
}

public class BatchMoveToFocusedRequest : BatchCollection<MoveToFocusedRequest>
{
    public BatchMoveToFocusedRequest(IEnumerable<MoveToFocusedRequest> collection) : base(collection)
    {
    }
}
