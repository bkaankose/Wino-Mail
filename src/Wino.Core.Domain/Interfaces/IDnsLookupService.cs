#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// DNS queries for account setup. Failures and missing records return empty results; only cancellation throws.
/// </summary>
public interface IDnsLookupService
{
    /// <summary>The raw answer data of every record of <paramref name="type"/> for <paramref name="name"/>.</summary>
    Task<IReadOnlyList<string>> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default);

    /// <summary>The domain's MX records, most preferred first.</summary>
    Task<IReadOnlyList<DnsMxRecord>> GetMxRecordsAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>The SRV records for <paramref name="name"/> in the order RFC 2782 tries them.</summary>
    Task<IReadOnlyList<DnsSrvRecord>> GetSrvRecordsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Whether the host has an A or AAAA record. Local names and IP literals always count as resolvable.</summary>
    Task<bool> HasAddressRecordAsync(string host, CancellationToken cancellationToken = default);
}
