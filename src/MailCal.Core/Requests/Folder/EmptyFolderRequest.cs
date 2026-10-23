using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.UI;

namespace MailCal.Core.Requests.Folder;

public record EmptyFolderRequest(MailItemFolder Folder, List<MailCopy> MailsToDelete) : FolderRequestBase(Folder, FolderSynchronizerOperation.EmptyFolder), ICustomFolderSynchronizationRequest
{
    public bool ExcludeMustHaveFolders => false;
    public override void ApplyUIChanges()
    {
        var removedMails = MailsToDelete?
            .Where(item => item != null)
            .ToList();

        if (removedMails == null || removedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailRemovedMessage(removedMails, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        var addedMails = MailsToDelete?
            .Where(item => item != null)
            .ToList();

        if (addedMails == null || addedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailAddedMessage(addedMails, EntityUpdateSource.ClientReverted));
    }

    public List<Guid> SynchronizationFolderIds => [Folder.Id];
}
