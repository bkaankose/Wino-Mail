using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests
{
    public record ChangeFlagRequest(MailCopy Item, bool IsFlagged) : RequestBase<BatchMoveRequest>(Item, MailSynchronizerOperation.ChangeFlag),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchChangeFlagRequest(matchingItems, IsFlagged);

        public override void ApplyUIChanges()
        {
            Item.IsFlagged = IsFlagged;

            WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            Item.IsFlagged = !IsFlagged;

            WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record BatchChangeFlagRequest(IEnumerable<IRequest> Items, bool IsFlagged) : BatchRequestBase(Items, MailSynchronizerOperation.ChangeFlag)
    {
        public override void ApplyUIChanges()
        {
            Items.ForEach(item =>
            {
                item.Item.IsFlagged = IsFlagged;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item.Item));
            });
        }

        public override void RevertUIChanges()
        {
            Items.ForEach(item =>
            {
                item.Item.IsFlagged = !IsFlagged;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item.Item));
            });
        }
    }
}
