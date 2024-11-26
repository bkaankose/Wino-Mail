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
    /// Hard delete request. This request will delete the mail item from the server without moving it to the trash folder.
    /// </summary>
    /// <param name="MailItem">Item to delete permanently.</param>
    public record DeleteRequest(MailCopy MailItem) : MailRequestBase(MailItem),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Delete;

        public override void ApplyUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
        }
    }

    public class BatchDeleteRequest : BatchCollection<DeleteRequest>
    {
        public BatchDeleteRequest(IEnumerable<DeleteRequest> collection) : base(collection)
        {
        }
    }
}
