#nullable enable
namespace Wino.Core.Domain.Models.Connectivity;

public enum DnsRecordType
{
    A,
    AAAA,
    CNAME,
    MX,
    SRV,
    TXT
}

/// <summary>An MX record; a lower <see cref="Preference"/> is tried first.</summary>
public sealed record DnsMxRecord(ushort Preference, string Host);

/// <summary>An SRV record (RFC 2782): lower <see cref="Priority"/> wins, then higher <see cref="Weight"/>.</summary>
public sealed record DnsSrvRecord(ushort Priority, ushort Weight, int Port, string Target);
