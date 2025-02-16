using System;
using System.Collections.Generic;
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

    public override void ApplyUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
    }

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailAddedMessage(Item));
    }
}

public class BatchMoveRequest : BatchCollection<MoveRequest>, IUIChangeRequest
{
    public BatchMoveRequest(IEnumerable<MoveRequest> collection) : base(collection)
    {
    }
}
