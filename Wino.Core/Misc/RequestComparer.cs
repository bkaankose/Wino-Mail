using System.Collections.Generic;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Requests;

namespace Wino.Core.Misc
{
    /// <summary>
    /// This is incomplete.
    /// </summary>
    internal class RequestComparer : IEqualityComparer<IRequestBase>
    {
        public bool Equals(IRequestBase x, IRequestBase y)
        {
            if (x is MoveRequest sourceMoveRequest && y is MoveRequest targetMoveRequest)
            {
                return sourceMoveRequest.FromFolder.Id == targetMoveRequest.FromFolder.Id && sourceMoveRequest.ToFolder.Id == targetMoveRequest.ToFolder.Id;
            }
            else if (x is ChangeFlagRequest sourceFlagRequest && y is ChangeFlagRequest targetFlagRequest)
            {
                return sourceFlagRequest.IsFlagged == targetFlagRequest.IsFlagged;
            }
            else if (x is MarkReadRequest sourceMarkReadRequest && y is MarkReadRequest targetMarkReadRequest)
            {
                return sourceMarkReadRequest.Item.IsRead == targetMarkReadRequest.Item.IsRead;
            }
            else if (x is DeleteRequest sourceDeleteRequest && y is DeleteRequest targetDeleteRequest)
            {
                return sourceDeleteRequest.MailItem.AssignedFolder.Id == targetDeleteRequest.MailItem.AssignedFolder.Id;
            }

            return true;
        }

        public int GetHashCode(IRequestBase obj) => obj.Operation.GetHashCode();
    }
}
