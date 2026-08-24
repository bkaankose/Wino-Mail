using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class ContactAddressBook
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid MailAccountId { get; set; }
    [Indexed] public ContactSourceKind SourceKind { get; set; }
    public string RemoteId { get; set; }
    public string ParentRemoteId { get; set; }
    public string DisplayName { get; set; }
    public bool IsDefault { get; set; }
    public string DeltaToken { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
}
