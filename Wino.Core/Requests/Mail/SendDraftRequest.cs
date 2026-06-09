using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;
using Wino.Core.Domain.Models.Messaging;

namespace Wino.Core.Requests.Mail;

public record SendDraftRequest(SendDraftPreparationRequest Request)
    : MailRequestBase(Request.MailItem),
    ICustomFolderSynchronizationRequest
{
    private int isUiChangeApplied;

    public List<Guid> SynchronizationFolderIds
    {
        get
        {
            var folderIds = new List<Guid> { Request.DraftFolder.Id };

            if (Request.SentFolder != null)
            {
                folderIds.Add(Request.SentFolder.Id);
            }

            return folderIds;
        }
    }

    public bool ExcludeMustHaveFolders => false;

    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.Send;

    public override void ApplyUIChanges()
    {
        if (Interlocked.Exchange(ref isUiChangeApplied, 1) == 1)
            return;

        UIMessagePublisherProvider.Current.Publish(new MailRemovedMessage(Item, EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        if (Interlocked.Exchange(ref isUiChangeApplied, 0) == 0)
            return;

        UIMessagePublisherProvider.Current.Publish(new MailAddedMessage(Item, EntityUpdateSource.ClientReverted));
    }
}
