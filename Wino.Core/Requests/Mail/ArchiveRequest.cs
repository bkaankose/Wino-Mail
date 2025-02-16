using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail
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
    public record ArchiveRequest(bool IsArchiving, MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder = null)
        : MailRequestBase(Item), ICustomFolderSynchronizationRequest
    {
        public bool ExcludeMustHaveFolders => false;
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

        public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Archive;

        public override void ApplyUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
        }
    }

    public class BatchArchiveRequest : BatchCollection<ArchiveRequest>
    {
        public BatchArchiveRequest(IEnumerable<ArchiveRequest> collection) : base(collection)
        {
        }
    }
}
