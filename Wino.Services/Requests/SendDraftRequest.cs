using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Requests;
using Wino.Messaging.Server;

namespace Wino.Services.Requests
{
    public record SendDraftRequest(SendDraftPreparationRequest Request)
        : RequestBase<BatchMarkReadRequest>(Request.MailItem, MailSynchronizerOperation.Send),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds
        {
            get
            {
                var folderIds = new List<Guid> { Request.DraftFolder.Id };

                if (Request.SentFolder != null)
                {
                    folderIds.Add(Request.SentFolder.Id);
                }

                return folderIds;
            }
        }

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchSendDraftRequestRequest(matchingItems, Request);

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
    public record BatchSendDraftRequestRequest(IEnumerable<IRequest> Items, SendDraftPreparationRequest Request) : BatchRequestBase(Items, MailSynchronizerOperation.Send)
    {
        public override void ApplyUIChanges()
        {
            Items.ForEach(item => WeakReferenceMessenger.Default.Send(new MailRemovedMessage(item.Item)));
        }

        public override void RevertUIChanges()
        {
            Items.ForEach(item => WeakReferenceMessenger.Default.Send(new MailAddedMessage(item.Item)));
        }

        public override bool DelayExecution => true;
    }
}
