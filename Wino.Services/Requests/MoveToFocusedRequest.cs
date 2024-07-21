using System.Collections.Generic;
using System.ComponentModel;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Requests;

namespace Wino.Services.Requests
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
