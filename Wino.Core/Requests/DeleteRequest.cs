using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests
{
    /// <summary>
    /// Hard delete request. This request will delete the mail item from the server without moving it to the trash folder.
    /// </summary>
    /// <param name="MailItem">Item to delete permanently.</param>
    public record DeleteRequest(MailCopy MailItem) : RequestBase<BatchDeleteRequest>(MailItem, MailSynchronizerOperation.Delete),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchDeleteRequest(matchingItems);

        public override void ApplyUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record class BatchDeleteRequest(IEnumerable<IRequest> Items) : BatchRequestBase(Items, MailSynchronizerOperation.Delete)
    {
        public override void ApplyUIChanges()
        {
            Items.ForEach(item => WeakReferenceMessenger.Default.Send(new MailRemovedMessage(item.Item)));
        }

        public override void RevertUIChanges()
        {
            Items.ForEach(item => WeakReferenceMessenger.Default.Send(new MailAddedMessage(item.Item)));
        }
    }
}
