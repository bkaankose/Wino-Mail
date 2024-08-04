using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests
{
    public record CreateDraftRequest(DraftPreperationRequest DraftPreperationRequest)
        : RequestBase<BatchCreateDraftRequest>(DraftPreperationRequest.CreatedLocalDraftCopy, MailSynchronizerOperation.CreateDraft),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds =>
        [
            DraftPreperationRequest.CreatedLocalDraftCopy.AssignedFolder.Id
        ];

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchCreateDraftRequest(matchingItems, DraftPreperationRequest);

        public override void ApplyUIChanges()
        {
            // No need for it since Draft folder is automatically navigated and draft item is added + selected.
            // We only need to revert changes in case of network fails to create the draft.
        }

        public override void RevertUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public record class BatchCreateDraftRequest(IEnumerable<IRequest> Items, DraftPreperationRequest DraftPreperationRequest)
        : BatchRequestBase(Items, MailSynchronizerOperation.CreateDraft)
    {
        public override void ApplyUIChanges()
        {
            // No need for it since Draft folder is automatically navigated and draft item is added + selected.
            // We only need to revert changes in case of network fails to create the draft.
        }

        public override void RevertUIChanges()
        {
            Items.ForEach(item => WeakReferenceMessenger.Default.Send(new MailRemovedMessage(item.Item)));
        }
    }
}
