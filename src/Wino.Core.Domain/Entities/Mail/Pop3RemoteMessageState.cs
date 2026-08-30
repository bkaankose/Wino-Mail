using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// Records a server UIDL that was intentionally evaluated but not imported, for example because
/// it predates the configured initial range. This keeps later polling incremental without using
/// unstable POP3 sequence numbers.
/// </summary>
public sealed class Pop3RemoteMessageState
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [Indexed("IX_Pop3State_AccountUidl", 1, Unique = true)]
    public Guid AccountId { get; set; }

    [Indexed("IX_Pop3State_AccountUidl", 2, Unique = true)]
    public string Uidl { get; set; }

    public DateTime EvaluatedAtUtc { get; set; }
}
