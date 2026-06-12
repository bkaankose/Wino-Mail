using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Folder;

public record MarkFolderAsReadRequest(MailItemFolder Folder, List<MailCopy> MailsToMarkRead) : FolderRequestBase(Folder, FolderSynchronizerOperation.MarkFolderRead), ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [Folder.Id];

    public bool ExcludeMustHaveFolders => true;

    public override void ApplyUIChanges()
    {
        var updatedMails = MailsToMarkRead?
            .Where(item => item != null && !item.IsRead)
            .Select(item => new MailStateChange(item.UniqueId, IsRead: true))
            .ToList();

        if (updatedMails == null || updatedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailStateUpdatedMessage(
            updatedMails,
            EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        var updatedMails = MailsToMarkRead?
            .Where(item => item != null && !item.IsRead)
            .Select(item => new MailStateChange(item.UniqueId, IsRead: false))
            .ToList();

        if (updatedMails == null || updatedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailStateUpdatedMessage(
            updatedMails,
            EntityUpdateSource.ClientReverted));
    }
}
