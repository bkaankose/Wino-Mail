using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// An address the account has corresponded with. Wino maintains it from synchronized mail
/// to rank compose suggestions; it is never shown as a contact and never leaves the device.
/// </summary>
public class RecipientHistory
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AccountId { get; set; }
    [Indexed] public string NormalizedAddress { get; set; }
    public string Address { get; set; }
    public string DisplayName { get; set; }
    public int SentCount { get; set; }
    public int ReceivedCount { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public DateTime? LastReceivedUtc { get; set; }

    /// <summary>
    /// Set when the user asked Wino not to suggest this address. Recording new mail never clears it.
    /// </summary>
    public bool IsSuppressed { get; set; }

    [Ignore]
    public DateTime? LastInteractionUtc
        => LastSentUtc is { } sent && LastReceivedUtc is { } received
            ? (sent > received ? sent : received)
            : LastSentUtc ?? LastReceivedUtc;
}
