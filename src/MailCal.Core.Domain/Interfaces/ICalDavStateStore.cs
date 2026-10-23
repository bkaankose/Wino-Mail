#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Persists <see cref="CalDavAccountState"/> per account as a JSON cache file beside the ICS
/// store. A missing or unreadable file reads as an empty state, which triggers rediscovery.
/// </summary>
public interface ICalDavStateStore
{
    /// <summary>Never returns null. Every call returns a new instance the caller may change.</summary>
    Task<CalDavAccountState> GetAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task SaveAsync(Guid accountId, CalDavAccountState state, CancellationToken cancellationToken = default);

    /// <summary>Reads, changes and writes the state under the account's lock.</summary>
    Task UpdateAsync(Guid accountId, Action<CalDavAccountState> update, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default);
}
