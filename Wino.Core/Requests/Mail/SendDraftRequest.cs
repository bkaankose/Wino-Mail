using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail
{
    public record SendDraftRequest(SendDraftPreparationRequest Request)
        : MailRequestBase(Request.MailItem),
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

        public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Send;

        public override void ApplyUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
        }

        public override void RevertUIChanges()
        {
            WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
        }
    }
}
