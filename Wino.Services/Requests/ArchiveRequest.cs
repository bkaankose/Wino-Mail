using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Requests;
using Wino.Messaging.Server;

namespace Wino.Services.Requests
{
    /// <summary>
    /// Archive message request.
    /// By default, the message will be moved to the Archive folder.
    /// For Gmail, 'Archive' label will be removed from the message.
    /// </summary>
    /// <param name="IsArchiving">Whether are archiving or unarchiving</param>
    /// <param name="Item">Mail to archive</param>
    /// <param name="FromFolder">Source folder.</param>
    /// <param name="ToFolder">Optional Target folder. Required for ImapSynchronizer and OutlookSynchronizer.</param>
    public record ArchiveRequest(bool IsArchiving, MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder = null) : RequestBase<BatchArchiveRequest>(Item, MailSynchronizerOperation.Archive), ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds
        {
            get
            {
                var folderIds = new List<Guid> { FromFolder.Id };

                if (ToFolder != null)
                {
                    folderIds.Add(ToFolder.Id);
                }

                return folderIds;
            }
        }

        public override IBatchChangeRequest CreateBatch(IEnumerable<IRequest> matchingItems)
            => new BatchArchiveRequest(IsArchiving, matchingItems, FromFolder, ToFolder);

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
    public record BatchArchiveRequest(bool IsArchiving, IEnumerable<IRequest> Items, MailItemFolder FromFolder, MailItemFolder ToFolder = null) : BatchRequestBase(Items, MailSynchronizerOperation.Archive)
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
