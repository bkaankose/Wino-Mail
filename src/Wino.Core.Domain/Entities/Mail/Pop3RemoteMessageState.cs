using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// Records a server UIDL Wino has evaluated: either imported, or skipped because it predates the
/// initial range. This keeps later polling incremental without unstable POP3 sequence numbers, and
/// a local deletion never brings an imported message back.
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

    /// <summary>
    /// Whether the message was downloaded. Only imported messages may be deleted from the server by
    /// the retention options; skipped ones were never saved on this device.
    /// </summary>
    public bool IsImported { get; set; }
}
