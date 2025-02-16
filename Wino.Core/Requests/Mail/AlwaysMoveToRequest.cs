using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests.Mail
{
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
}
