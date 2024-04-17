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
    public record MoveRequest(MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder)
        : RequestBase<BatchMoveRequest>(Item, MailSynchronizerOperation.Move), ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => new() { FromFolder.Id, ToFolder.Id };

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchMoveRequest(matchingItems, FromFolder, ToFolder);

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
    public record BatchMoveRequest(IEnumerable<IRequest> Items, MailItemFolder FromFolder, MailItemFolder ToFolder) : BatchRequestBase(Items, MailSynchronizerOperation.Move)
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
