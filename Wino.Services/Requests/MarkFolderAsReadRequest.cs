using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Requests;
using Wino.Messaging.Server;

namespace Wino.Services.Requests
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

        public override bool DelayExecution => false;

        public List<Guid> SynchronizationFolderIds => [Folder.Id];
    }
}
