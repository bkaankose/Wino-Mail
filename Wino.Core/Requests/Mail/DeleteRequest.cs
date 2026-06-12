using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail;

/// <summary>
/// Hard delete request. This request will delete the mail item from the server without moving it to the trash folder.
/// </summary>
/// <param name="MailItem">Item to delete permanently.</param>
public record DeleteRequest(MailCopy MailItem) : MailRequestBase(MailItem),
    ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => [Item.FolderId];
    public bool ExcludeMustHaveFolders => false;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Delete;
    public override object GroupingKey() => (Operation, Item.FolderId);

    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item, EntityUpdateSource.ClientReverted));
    }
}

public class BatchDeleteRequest : BatchCollection<DeleteRequest>
{
    public BatchDeleteRequest(IEnumerable<DeleteRequest> collection) : base(collection)
    {
    }

    public override void ApplyUIChanges()
    {
        var removedMails = this.Select(x => x.Item).Where(x => x != null).ToList();
        if (removedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailRemovedMessage(removedMails, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        var addedMails = this.Select(x => x.Item).Where(x => x != null).ToList();
        if (addedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailAddedMessage(addedMails, EntityUpdateSource.ClientReverted));
    }
}
