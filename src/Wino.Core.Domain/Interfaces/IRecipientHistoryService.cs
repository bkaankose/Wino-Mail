using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// An address and the name it was written with.
/// </summary>
public readonly record struct RecipientAddress(string Address, string DisplayName);

/// <summary>
/// Remembers who an account writes to and hears from, so compose can suggest them.
/// The history is separate from contacts: it never appears on the Contacts page.
/// </summary>
public interface IRecipientHistoryService
{
    /// <summary>
    /// Records a received mail's sender. Automated and machine generated senders are ignored.
    /// </summary>
    Task RecordReceivedAsync(Guid accountId, string address, string displayName, DateTime whenUtc);

    /// <summary>
    /// Records the recipients of a mail the account sent. The user chose these addresses,
    /// so only structurally invalid ones are ignored.
    /// </summary>
    Task RecordSentAsync(Guid accountId, IReadOnlyCollection<RecipientAddress> recipients, DateTime whenUtc);

    /// <summary>
    /// Stops suggesting an address for the account. New mail from or to it does not undo this.
    /// </summary>
    Task SuppressAsync(Guid accountId, string address);

    /// <summary>
    /// Unsuppressed history rows whose address or name contains the query, most used first.
    /// </summary>
    Task<List<RecipientHistory>> SearchAsync(Guid accountId, string query, int limit = 30);

    /// <summary>
    /// Forgets the account's history, including suppressions.
    /// </summary>
    Task ClearAsync(Guid accountId);
}
