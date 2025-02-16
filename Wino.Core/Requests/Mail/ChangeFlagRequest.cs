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
    public record ChangeFlagRequest(MailCopy Item, bool IsFlagged) : MailRequestBase(Item),
        ICustomFolderSynchronizationRequest
    {
        public List<Guid> SynchronizationFolderIds => [Item.FolderId];

        public bool ExcludeMustHaveFolders => true;

        public override MailSynchronizerOperation Operation => MailSynchronizerOperation.ChangeFlag;

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

    public class BatchChangeFlagRequest : BatchCollection<ChangeFlagRequest>
    {
        public BatchChangeFlagRequest(IEnumerable<ChangeFlagRequest> collection) : base(collection)
        {
        }
    }
}
