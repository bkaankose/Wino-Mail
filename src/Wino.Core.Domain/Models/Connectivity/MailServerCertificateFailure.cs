using System;
using System.Net.Security;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Connectivity;

public sealed class MailServerCertificateFailure
{
    public MailServerProtocol Protocol { get; init; }
    public string Host { get; init; }
    public int Port { get; init; }
    public SslPolicyErrors PolicyErrors { get; init; }
    public int ChainStatusFlags { get; init; }
    public string ChainStatusDetails { get; init; }
    public string Subject { get; init; }
    public string SubjectAlternativeNames { get; init; }
    public string Issuer { get; init; }
    public DateTime ValidFromUtc { get; init; }
    public DateTime ValidToUtc { get; init; }
    public string CertificateSha256 { get; init; }
    public byte[] CertificateRawData { get; init; }
    public bool CanTrust { get; init; }

    public MailServerCertificateTrust CreateTrust(Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        CertificateSha256 = CertificateSha256,
        CertificateRawData = CertificateRawData,
        Subject = Subject,
        SubjectAlternativeNames = SubjectAlternativeNames,
        Issuer = Issuer,
        ValidFromUtc = ValidFromUtc,
        ValidToUtc = ValidToUtc,
        AcceptedChainStatusFlags = ChainStatusFlags,
        AcceptedAtUtc = DateTime.UtcNow
    };
}
