using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests
{
    public record EmptyFolderRequest(MailItemFolder Folder, List<MailCopy> MailsToDelete) : FolderRequestBase(Folder, MailSynchronizerOperation.EmptyFolder), ICustomFolderSynchronizationRequest
    {
        public override void ApplyUIChanges()
        {
            foreach (var item in MailsToDelete)
            {
                WeakReferenceMessenger.Default.Send(new MailRemovedMessage(item));
            }
        }

        public override void RevertUIChanges()
        {
            foreach (var item in MailsToDelete)
            {
                WeakReferenceMessenger.Default.Send(new MailAddedMessage(item));
            }
        }

        public List<Guid> SynchronizationFolderIds => [Folder.Id];
    }
}
