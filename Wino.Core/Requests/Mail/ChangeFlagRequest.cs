using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail;

public record ChangeFlagRequest(MailCopy Item, bool IsFlagged) : MailRequestBase(Item),
    ICustomFolderSynchronizationRequest
{
    private readonly bool _originalIsFlagged = Item.IsFlagged;

    public List<Guid> SynchronizationFolderIds => [Item.FolderId];

    public bool ExcludeMustHaveFolders => true;

    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.ChangeFlag;

    /// <summary>
    /// Gets whether this request represents an actual state change.
    /// If the mail is already in the desired flagged state, no change is needed.
    /// </summary>
    public bool IsNoOp { get; } = Item.IsFlagged == IsFlagged;

    public override void ApplyUIChanges()
    {
        // Skip UI update if the mail is already in the desired state
        if (IsNoOp) return;

        Item.IsFlagged = IsFlagged;

        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item, EntityUpdateSource.ClientUpdated, MailCopyChangeFlags.IsFlagged));
    }

    public override void RevertUIChanges()
    {
        // Skip UI revert if this was a no-op request
        if (IsNoOp) return;

        Item.IsFlagged = _originalIsFlagged;

        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item, EntityUpdateSource.ClientReverted, MailCopyChangeFlags.IsFlagged));
    }
}

public class BatchChangeFlagRequest : BatchCollection<ChangeFlagRequest>
{
    public BatchChangeFlagRequest(IEnumerable<ChangeFlagRequest> collection) : base(collection)
    {
    }
}
