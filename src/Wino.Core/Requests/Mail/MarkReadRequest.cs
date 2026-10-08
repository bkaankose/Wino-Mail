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
    public bool OriginalIsRead => _originalIsRead;

    public override object GroupingKey() => (Operation, Item.FolderId, IsRead);

    public override void ApplyUIChanges()
    {
        if (IsNoOp) return;

        WeakReferenceMessenger.Default.Send(new MailStateUpdatedMessage(
            new MailStateChange(Item.UniqueId, IsRead: IsRead),
            EntityUpdateSource.ClientUpdated));
    }

    public override void RevertUIChanges()
    {
        if (IsNoOp) return;

        WeakReferenceMessenger.Default.Send(new MailStateUpdatedMessage(
            new MailStateChange(Item.UniqueId, IsRead: _originalIsRead),
            EntityUpdateSource.ClientReverted));
    }
}

public class BatchMarkReadRequest : BatchCollection<MarkReadRequest>
{
    public BatchMarkReadRequest(IEnumerable<MarkReadRequest> collection) : base(collection)
    {
    }

    public override void ApplyUIChanges()
    {
        var updatedMails = this
            .Where(x => !x.IsNoOp)
            .Select(x => new MailStateChange(x.Item.UniqueId, IsRead: x.IsRead))
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
            .Select(x => new MailStateChange(x.Item.UniqueId, IsRead: x.OriginalIsRead))
            .ToList();

        if (updatedMails.Count == 0)
            return;

        WeakReferenceMessenger.Default.Send(new BulkMailStateUpdatedMessage(
            updatedMails,
            EntityUpdateSource.ClientReverted));
    }
}
