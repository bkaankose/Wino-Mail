using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>Account-scoped provider cursors shared by every task list.</summary>
[Table("TaskSyncState")]
public sealed class AccountTaskSyncState
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Indexed]
    public Guid MailAccountId { get; set; }

    [Indexed]
    public TaskSourceKind SourceKind { get; set; }

    public string ListDeltaLink { get; set; }
    public string SubstrateGroupDeltaLink { get; set; }
    public string SubstrateFolderDeltaLink { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
}
