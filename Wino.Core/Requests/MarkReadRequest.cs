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
    public record MarkReadRequest(MailCopy Item, bool IsRead) : RequestBase<BatchMarkReadRequest>(Item, MailSynchronizerOperation.MarkRead),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchMarkReadRequest(matchingItems, IsRead);

        public override void ApplyUIChanges()
        {
            Item.IsRead = IsRead;

            WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            Item.IsRead = !IsRead;

            WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item));
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record BatchMarkReadRequest(IEnumerable<IRequest> Items, bool IsRead) : BatchRequestBase(Items, MailSynchronizerOperation.MarkRead)
    {
        public override void ApplyUIChanges()
        {
            Items.ForEach(item =>
            {
                item.Item.IsRead = IsRead;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item.Item));
            });
        }

        public override void RevertUIChanges()
        {
            Items.ForEach(item =>
            {
                item.Item.IsRead = !IsRead;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item.Item));
            });
        }
    }
}
