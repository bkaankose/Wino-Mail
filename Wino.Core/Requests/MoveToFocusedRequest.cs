using System.Collections.Generic;
using System.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record MoveToFocusedRequest(MailCopy Item, bool MoveToFocused) : RequestBase<BatchMoveRequest>(Item, MailSynchronizerOperation.Move)
    {
        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchMoveToFocusedRequest(matchingItems, MoveToFocused);

        public override void ApplyUIChanges() { }

        public override void RevertUIChanges() { }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record BatchMoveToFocusedRequest(IEnumerable<IRequest> Items, bool MoveToFocused) : BatchRequestBase(Items, MailSynchronizerOperation.Move)
    {
        public override void ApplyUIChanges() { }

        public override void RevertUIChanges() { }
    }
}
