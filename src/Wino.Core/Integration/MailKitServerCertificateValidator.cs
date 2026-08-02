using System;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Integration;

internal static class MailKitServerCertificateValidator
{
    private const X509ChainStatusFlags TrustableChainErrors =
        X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain;

    public static bool Validate(
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors,
        MailServerProtocol protocol,
        string host,
        int port,
        MailServerCertificateTrust storedTrust = null,
        MailServerCertificateTrust transientTrust = null)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        var failure = CreateFailure(certificate, chain, sslPolicyErrors, protocol, host, port);
        var trust = transientTrust ?? storedTrust;

        if (failure.CanTrust && TrustMatches(trust, failure))
            return true;

        throw new MailServerCertificateException(failure);
    }

    internal static MailServerCertificateFailure CreateFailure(
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors,
        MailServerProtocol protocol,
        string host,
        int port)
    {
        var certificate2 = certificate == null ? null : new X509Certificate2(certificate);
        var chainStatuses = chain?.ChainStatus ?? [];
        var chainFlags = chainStatuses.Aggregate(X509ChainStatusFlags.NoError, (current, status) => current | status.Status);
        var now = DateTime.UtcNow;
        var hasOnlyTrustableChainErrors = chainFlags != X509ChainStatusFlags.NoError &&
                                          (chainFlags & ~TrustableChainErrors) == X509ChainStatusFlags.NoError;
        var hasOnlyChainPolicyError = sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors;
        var isWithinValidity = certificate2 != null && now >= certificate2.NotBefore.ToUniversalTime() && now <= certificate2.NotAfter.ToUniversalTime();

        return new MailServerCertificateFailure
        {
            Protocol = protocol,
            Host = NormalizeHost(host),
            Port = port,
            PolicyErrors = sslPolicyErrors,
            ChainStatusFlags = (int)chainFlags,
            ChainStatusDetails = string.Join(Environment.NewLine, chainStatuses.Select(status => status.StatusInformation?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value))),
            Subject = certificate2?.Subject ?? string.Empty,
            SubjectAlternativeNames = GetSubjectAlternativeNames(certificate2),
            Issuer = certificate2?.Issuer ?? string.Empty,
            ValidFromUtc = certificate2?.NotBefore.ToUniversalTime() ?? DateTime.MinValue,
            ValidToUtc = certificate2?.NotAfter.ToUniversalTime() ?? DateTime.MinValue,
            CertificateSha256 = certificate2 == null ? string.Empty : Convert.ToHexString(SHA256.HashData(certificate2.RawData)),
            CertificateRawData = certificate2?.RawData ?? [],
            CanTrust = hasOnlyChainPolicyError && hasOnlyTrustableChainErrors && isWithinValidity
        };
    }

    private static bool TrustMatches(MailServerCertificateTrust trust, MailServerCertificateFailure failure)
    {
        if (trust == null || failure == null || DateTime.UtcNow > trust.ValidToUtc || DateTime.UtcNow < trust.ValidFromUtc)
            return false;

        if (trust.Protocol != failure.Protocol ||
            !string.Equals(NormalizeHost(trust.Host), NormalizeHost(failure.Host), StringComparison.Ordinal) ||
            trust.Port != failure.Port ||
            trust.AcceptedChainStatusFlags != failure.ChainStatusFlags)
        {
            return false;
        }

        try
        {
            var trustedHash = Convert.FromHexString(trust.CertificateSha256 ?? string.Empty);
            var presentedHash = Convert.FromHexString(failure.CertificateSha256 ?? string.Empty);
            return trustedHash.Length == presentedHash.Length && CryptographicOperations.FixedTimeEquals(trustedHash, presentedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string GetSubjectAlternativeNames(X509Certificate2 certificate)
        => certificate?.Extensions
            .OfType<X509Extension>()
            .FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17")?
            .Format(false) ?? string.Empty;

    private static string NormalizeHost(string host) => host?.Trim().ToLowerInvariant() ?? string.Empty;
}
