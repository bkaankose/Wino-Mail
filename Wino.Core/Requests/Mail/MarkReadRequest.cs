using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Messaging.UI;

namespace Wino.Core.Requests.Mail;

public record MarkReadRequest(MailCopy Item, bool IsRead) : MailRequestBase(Item), ICustomFolderSynchronizationRequest
{
    private readonly bool _originalIsRead = Item.IsRead;

    public List<Guid> SynchronizationFolderIds => [Item.FolderId];

    public override MailSynchronizerOperation Operation => MailSynchronizerOperation.MarkRead;

    public bool ExcludeMustHaveFolders => true;

    /// <summary>
    /// Gets whether this request represents an actual state change.
    /// If the mail is already in the desired read state, no change is needed.
    /// </summary>
    public bool IsNoOp { get; } = Item.IsRead == IsRead;

    public override void ApplyUIChanges()
    {
        // Skip UI update if the mail is already in the desired state
        if (IsNoOp) return;

        Item.IsRead = IsRead;

        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item, MailUpdateSource.ClientUpdated, MailCopyChangeFlags.IsRead));
    }

    public override void RevertUIChanges()
    {
        // Skip UI revert if this was a no-op request
        if (IsNoOp) return;

        Item.IsRead = _originalIsRead;

        WeakReferenceMessenger.Default.Send(new MailUpdatedMessage(Item, MailUpdateSource.ClientReverted, MailCopyChangeFlags.IsRead));
    }
}

public class BatchMarkReadRequest : BatchCollection<MarkReadRequest>
{
    public BatchMarkReadRequest(IEnumerable<MarkReadRequest> collection) : base(collection)
    {
    }
}
