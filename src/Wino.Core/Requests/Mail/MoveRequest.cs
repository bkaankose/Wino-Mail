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

public record MoveRequest(MailCopy Item, MailItemFolder FromFolder, MailItemFolder ToFolder)
    : MailRequestBase(Item), ICustomFolderSynchronizationRequest
{
    public List<Guid> SynchronizationFolderIds => new() { FromFolder.Id, ToFolder.Id };
    public bool ExcludeMustHaveFolders => false;
    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Move;
    public override object GroupingKey() => (Operation, FromFolder.Id, ToFolder?.Id ?? Guid.Empty);

    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item, EntityUpdateSource.ClientReverted));
    }
}

public class BatchMoveRequest : BatchCollection<MoveRequest>, IUIChangeRequest
{
    public BatchMoveRequest(IEnumerable<MoveRequest> collection) : base(collection)
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
