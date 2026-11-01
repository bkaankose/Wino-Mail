using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class MailServerCertificateTrust
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }
    public MailServerProtocol Protocol { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }
    public string CertificateSha256 { get; set; }
    public byte[] CertificateRawData { get; set; }
    public string Subject { get; set; }
    public string SubjectAlternativeNames { get; set; }
    public string Issuer { get; set; }
    public DateTime ValidFromUtc { get; set; }
    public DateTime ValidToUtc { get; set; }
    public int AcceptedChainStatusFlags { get; set; }

    // Null on rows saved before policy errors were recorded; those could only have been chain errors.
    public int? AcceptedPolicyErrors { get; set; }
    public DateTime AcceptedAtUtc { get; set; }
}
