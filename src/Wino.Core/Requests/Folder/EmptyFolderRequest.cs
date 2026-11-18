using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Messaging.UI;

namespace Wino.Core.Requests.Folder;

public record EmptyFolderRequest(MailItemFolder Folder, List<MailCopy> MailsToDelete) : FolderRequestBase(Folder, FolderSynchronizerOperation.EmptyFolder), ICustomFolderSynchronizationRequest
{
    public bool ExcludeMustHaveFolders => false;
    public override void ApplyUIChanges(IUIMessageSender sender)
    {
        var removedMails = MailsToDelete?
            .Where(item => item != null)
            .ToList();

        if (removedMails == null || removedMails.Count == 0)
            return;

        sender.Send(new BulkMailRemovedMessage(removedMails, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges(IUIMessageSender sender)
    {
        var addedMails = MailsToDelete?
            .Where(item => item != null)
            .ToList();

        if (addedMails == null || addedMails.Count == 0)
            return;

        sender.Send(new BulkMailAddedMessage(addedMails, EntityUpdateSource.ClientReverted));
    }

    public List<Guid> SynchronizationFolderIds => [Folder.Id];
}
