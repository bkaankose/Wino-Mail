using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

public sealed class Pop3PendingServerDeletion
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [Indexed("IX_Pop3Deletion_AccountUidl", 1, Unique = true)]
    public Guid AccountId { get; set; }

    [Indexed("IX_Pop3Deletion_AccountUidl", 2, Unique = true)]
    public string Uidl { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string LastError { get; set; }
}
