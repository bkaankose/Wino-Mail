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
