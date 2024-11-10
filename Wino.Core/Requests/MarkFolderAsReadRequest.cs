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
    public record MarkFolderAsReadRequest(MailItemFolder Folder, List<MailCopy> MailsToMarkRead) : FolderRequestBase(Folder, MailSynchronizerOperation.MarkFolderRead), ICustomFolderSynchronizationRequest
    {
        public override void ApplyUIChanges()
        {
            foreach (var item in MailsToMarkRead)
            {
                item.IsRead = true;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item));
            }
        }

        public override void RevertUIChanges()
        {
            foreach (var item in MailsToMarkRead)
            {
                item.IsRead = false;

                WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(item));
            }
        }

        public List<Guid> SynchronizationFolderIds => [Folder.Id];
    }
}
