using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Mail;

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
