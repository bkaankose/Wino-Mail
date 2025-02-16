using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail;

public record CreateDraftRequest(DraftPreparationRequest DraftPreperationRequest)
    : MailRequestBase(DraftPreperationRequest.CreatedLocalDraftCopy),
    ICustomFolderSynchronizationRequest
{
    public bool ExcludeMustHaveFolders => false;

    public List<Guid> SynchronizationFolderIds =>
    [
        DraftPreperationRequest.CreatedLocalDraftCopy.AssignedFolder.Id
    ];

    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.CreateDraft;

    public override void RevertUIChanges()
    {
        WeakReferenceMessenger.Default.Send(new MailRemovedMessage(Item));
    }
}
