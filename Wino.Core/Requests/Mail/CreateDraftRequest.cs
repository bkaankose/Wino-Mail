using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;

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
        // Keep local draft intact when create-draft synchronization fails.
        // This allows users to retry sending the local draft to the server.
    }
}
