using System.Collections.Generic;
using System.ComponentModel;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Requests;

namespace Wino.Services.Requests
{
    public record AlwaysMoveToRequest(MailCopy Item, bool MoveToFocused) : RequestBase<BatchMoveRequest>(Item, MailSynchronizerOperation.AlwaysMoveTo)
    {
        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchAlwaysMoveToRequest(matchingItems, MoveToFocused);

        public override void ApplyUIChanges()
        {

        }

        public override void RevertUIChanges()
        {

        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record BatchAlwaysMoveToRequest(IEnumerable<IRequest> Items, bool MoveToFocused) : BatchRequestBase(Items, MailSynchronizerOperation.AlwaysMoveTo)
    {
        public override void ApplyUIChanges()
        {

        }

        public override void RevertUIChanges()
        {

        }
    }
}
