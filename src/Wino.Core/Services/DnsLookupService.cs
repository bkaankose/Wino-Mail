#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Validation;

namespace Wino.Core.Services;

/// <summary>
/// DNS over HTTPS through Google's resolver, which answers MX, SRV and TXT queries the same way on every platform.
/// The local resolver is not used, so a hosts-file entry does not make a guessed host resolvable.
/// </summary>
public sealed class DnsLookupService : IDnsLookupService
{
    private const string GoogleDnsResolveUrl = "https://dns.google/resolve";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    private static readonly ILogger Logger = Log.ForContext<DnsLookupService>();

    private readonly HttpClient _httpClient;

    public DnsLookupService() : this(new HttpClient())
    {
    }

    public DnsLookupService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Array.Empty<string>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);

        try
        {
            var url = $"{GoogleDnsResolveUrl}?name={Uri.EscapeDataString(name.Trim())}&type={type}";
            using var response = await _httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("Answer", out var answerArray) ||
                answerArray.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var expectedType = (int)ToWireType(type);
            var values = new List<string>();

            foreach (var answer in answerArray.EnumerateArray())
            {
                // A CNAME chain comes back in the same Answer list; only records of the asked type count.
                if (answer.TryGetProperty("type", out var typeNode) &&
                    typeNode.ValueKind == JsonValueKind.Number &&
                    typeNode.GetInt32() != expectedType)
                {
                    continue;
                }

                if (answer.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.String)
                {
                    var data = dataNode.GetString();
                    if (!string.IsNullOrWhiteSpace(data))
                        values.Add(data);
                }
            }

            return values;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "DNS-over-HTTPS query failed for {QueryName} ({Type})", name, type);
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<DnsMxRecord>> GetMxRecordsAsync(string domain, CancellationToken cancellationToken = default)
    {
        var records = await QueryAsync(domain, DnsRecordType.MX, cancellationToken).ConfigureAwait(false);

        return records
            .Select(ParseMxRecord)
            .OfType<DnsMxRecord>()
            .OrderBy(record => record.Preference)
            .ToArray();
    }

    public async Task<IReadOnlyList<DnsSrvRecord>> GetSrvRecordsAsync(string name, CancellationToken cancellationToken = default)
    {
        var records = await QueryAsync(name, DnsRecordType.SRV, cancellationToken).ConfigureAwait(false);

        return records
            .Select(ParseSrvRecord)
            .OfType<DnsSrvRecord>()
            .OrderBy(record => record.Priority)
            .ThenByDescending(record => record.Weight)
            .ToArray();
    }

    public async Task<bool> HasAddressRecordAsync(string host, CancellationToken cancellationToken = default)
    {
        if (MailAccountAddressValidator.IsImplicitlyResolvableHost(host))
            return true;

        var aRecords = await QueryAsync(host, DnsRecordType.A, cancellationToken).ConfigureAwait(false);
        if (aRecords.Count > 0)
            return true;

        var aaaaRecords = await QueryAsync(host, DnsRecordType.AAAA, cancellationToken).ConfigureAwait(false);
        return aaaaRecords.Count > 0;
    }

    internal static DnsSrvRecord? ParseSrvRecord(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;

        if (!ushort.TryParse(parts[0], out var priority) ||
            !ushort.TryParse(parts[1], out var weight) ||
            !int.TryParse(parts[2], out var port))
        {
            return null;
        }

        var target = parts[3].Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(target))
            return null;

        return new DnsSrvRecord(priority, weight, port, target);
    }

    internal static DnsMxRecord? ParseMxRecord(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ushort.TryParse(parts[0], out var preference))
            return null;

        var target = parts[1].Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(target))
            return null;

        return new DnsMxRecord(preference, target);
    }

    private static ushort ToWireType(DnsRecordType type) => type switch
    {
        DnsRecordType.A => 1,
        DnsRecordType.CNAME => 5,
        DnsRecordType.MX => 15,
        DnsRecordType.TXT => 16,
        DnsRecordType.AAAA => 28,
        DnsRecordType.SRV => 33,
        _ => 0
    };
}
