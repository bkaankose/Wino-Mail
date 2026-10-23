using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Requests;
using MailCal.Messaging.UI;

namespace MailCal.Core.Requests.Mail;

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
    public bool OriginalIsFlagged => _originalIsFlagged;

    public override object GroupingKey() => (Operation, Item.FolderId, IsFlagged);

    public override void ApplyUIChanges()
    {
        if (IsNoOp) return;

        WeakReferenceMessenger.Default.Send(new MailStateUpdatedMessage(
            new MailStateChange(Item.UniqueId, IsFlagged: IsFlagged),
            EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        if (IsNoOp) return;

        WeakReferenceMessenger.Default.Send(new MailStateUpdatedMessage(
            new MailStateChange(Item.UniqueId, IsFlagged: _originalIsFlagged),
            EntityUpdateSource.ClientReverted));
    }
}

public class BatchChangeFlagRequest : BatchCollection<ChangeFlagRequest>
{
    public BatchChangeFlagRequest(IEnumerable<ChangeFlagRequest> collection) : base(collection)
    {
    }

    public override void ApplyUIChanges()
    {
        var updatedMails = this
            .Where(x => !x.IsNoOp)
            .Select(x => new MailStateChange(x.Item.UniqueId, IsFlagged: x.IsFlagged))
            .ToList();

        if (updatedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailStateUpdatedMessage(
            updatedMails,
            EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        var updatedMails = this
            .Where(x => !x.IsNoOp)
            .Select(x => new MailStateChange(x.Item.UniqueId, IsFlagged: x.OriginalIsFlagged))
            .ToList();

        if (updatedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailStateUpdatedMessage(
            updatedMails,
            EntityUpdateSource.ClientReverted));
    }
}
