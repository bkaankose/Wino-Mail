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
    public record MarkReadRequest(MailCopy Item, bool IsRead) : MailRequestBase(Item), ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public override MailSynchronizerOperation Operation => MailSynchronizerOperation.MarkRead;

        public bool ExcludeMustHaveFolders => true;

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

    public class BatchMarkReadRequest : BatchCollection<MarkReadRequest>
    {
        public BatchMarkReadRequest(IEnumerable<MarkReadRequest> collection) : base(collection)
        {
        }
    }
}
