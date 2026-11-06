#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services;

/// <summary>
/// Decides whether a server certificate that failed system validation is accepted for IMAP, SMTP, POP3, CalDAV and
/// CardDAV, and how risky accepting it would be.
/// </summary>
public static class ServerCertificateValidator
{
    private const X509ChainStatusFlags NormalChainFlags =
        X509ChainStatusFlags.UntrustedRoot |
        X509ChainStatusFlags.PartialChain |
        X509ChainStatusFlags.NotTimeValid |
        X509ChainStatusFlags.RevocationStatusUnknown |
        X509ChainStatusFlags.OfflineRevocation;

    private const X509ChainStatusFlags DangerousChainFlags =
        X509ChainStatusFlags.Revoked |
        X509ChainStatusFlags.NotSignatureValid |
        X509ChainStatusFlags.CtlNotSignatureValid;

    private const X509ChainStatusFlags RevocationUnknownFlags =
        X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    private const SslPolicyErrors NormalPolicyErrors =
        SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;

    // Whether a revocation server answered changes between connections, so it must not invalidate a pin.
    private const int PinMask = ~(int)RevocationUnknownFlags;

    private static readonly ConcurrentDictionary<string, string> LoggedRejections = new(StringComparer.Ordinal);
    private static readonly ILogger Logger = Log.ForContext(typeof(ServerCertificateValidator));

    /// <summary>MailKit callback: true when accepted, otherwise throws <see cref="MailServerCertificateException"/>.</summary>
    public static bool Validate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors,
        MailServerProtocol protocol,
        string host,
        int port,
        MailServerCertificateTrust? storedTrust = null,
        MailServerCertificateTrust? transientTrust = null)
    {
        var failure = Evaluate(certificate, chain, sslPolicyErrors, protocol, host, port, storedTrust, transientTrust);
        if (failure == null)
            return true;

        throw new MailServerCertificateException(failure);
    }

    /// <summary>Null when the certificate is accepted, otherwise the failure the user can be asked about.</summary>
    public static MailServerCertificateFailure? Evaluate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors,
        MailServerProtocol protocol,
        string host,
        int port,
        MailServerCertificateTrust? storedTrust = null,
        MailServerCertificateTrust? transientTrust = null)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return null;

        var failure = CreateFailure(certificate, chain, sslPolicyErrors, protocol, host, port);
        var trust = transientTrust ?? storedTrust;

        if (failure.CanTrust && TrustMatches(trust, failure))
            return null;

        if (failure.Tier != MailServerCertificateTrustTier.Normal || trust != null)
            LogRejection(failure, trust != null);

        return failure;
    }

    public static MailServerCertificateFailure CreateFailure(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors,
        MailServerProtocol protocol,
        string host,
        int port)
    {
        var certificate2 = certificate == null ? null : certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        var chainStatuses = chain?.ChainStatus ?? [];
        var chainFlags = chainStatuses.Aggregate(X509ChainStatusFlags.NoError, (current, status) => current | status.Status);
        var (tier, problems) = Classify(certificate2, chainFlags, sslPolicyErrors);

        return new MailServerCertificateFailure
        {
            Protocol = protocol,
            Host = NormalizeHost(host),
            Port = port,
            PolicyErrors = sslPolicyErrors,
            ChainStatusFlags = (int)chainFlags,
            ChainStatusDetails = string.Join(Environment.NewLine, chainStatuses
                .Select(status => status.StatusInformation?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            Subject = certificate2?.Subject ?? string.Empty,
            SubjectAlternativeNames = GetSubjectAlternativeNames(certificate2),
            Issuer = certificate2?.Issuer ?? string.Empty,
            ValidFromUtc = certificate2?.NotBefore.ToUniversalTime() ?? DateTime.MinValue,
            ValidToUtc = certificate2?.NotAfter.ToUniversalTime() ?? DateTime.MinValue,
            CertificateSha256 = certificate2 == null ? string.Empty : Convert.ToHexString(SHA256.HashData(certificate2.RawData)),
            CertificateRawData = certificate2?.RawData ?? [],
            Tier = tier,
            Problems = problems
        };
    }

    private static (MailServerCertificateTrustTier Tier, MailServerCertificateProblem Problems) Classify(
        X509Certificate2? certificate,
        X509ChainStatusFlags chainFlags,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate == null || (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return (MailServerCertificateTrustTier.NotPossible, MailServerCertificateProblem.NoCertificate);

        var problems = MailServerCertificateProblem.None;

        if (chainFlags.HasFlag(X509ChainStatusFlags.UntrustedRoot))
            problems |= MailServerCertificateProblem.UntrustedRoot;
        if (chainFlags.HasFlag(X509ChainStatusFlags.PartialChain))
            problems |= MailServerCertificateProblem.PartialChain;
        if (chainFlags.HasFlag(X509ChainStatusFlags.NotTimeValid))
        {
            problems |= DateTime.UtcNow < certificate.NotBefore.ToUniversalTime()
                ? MailServerCertificateProblem.NotYetValid
                : MailServerCertificateProblem.Expired;
        }
        if ((chainFlags & RevocationUnknownFlags) != 0)
            problems |= MailServerCertificateProblem.RevocationUnknown;
        if (chainFlags.HasFlag(X509ChainStatusFlags.Revoked))
            problems |= MailServerCertificateProblem.Revoked;
        if ((chainFlags & (X509ChainStatusFlags.NotSignatureValid | X509ChainStatusFlags.CtlNotSignatureValid)) != 0)
            problems |= MailServerCertificateProblem.InvalidSignature;
        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            problems |= MailServerCertificateProblem.HostNameMismatch;

        var residualChainFlags = chainFlags & ~(NormalChainFlags | DangerousChainFlags);
        var residualPolicyErrors = sslPolicyErrors & ~NormalPolicyErrors;
        if (residualChainFlags != 0 || residualPolicyErrors != 0)
            problems |= MailServerCertificateProblem.Other;

        // Native HTTP handlers (Android, iOS) can report a chain error without any chain status.
        if (chainFlags == X509ChainStatusFlags.NoError && (sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            problems |= MailServerCertificateProblem.UnverifiedChain;

        var isDangerous = (chainFlags & DangerousChainFlags) != 0 ||
                          (chainFlags & ~NormalChainFlags) != 0 ||
                          residualPolicyErrors != 0;

        return (isDangerous ? MailServerCertificateTrustTier.Dangerous : MailServerCertificateTrustTier.Normal, problems);
    }

    private static bool TrustMatches(MailServerCertificateTrust? trust, MailServerCertificateFailure failure)
    {
        if (trust == null)
            return false;

        var acceptedPolicyErrors = trust.AcceptedPolicyErrors ?? (int)SslPolicyErrors.RemoteCertificateChainErrors;

        if (trust.Protocol != failure.Protocol ||
            !string.Equals(NormalizeHost(trust.Host), NormalizeHost(failure.Host), StringComparison.Ordinal) ||
            trust.Port != failure.Port ||
            (trust.AcceptedChainStatusFlags & PinMask) != (failure.ChainStatusFlags & PinMask) ||
            acceptedPolicyErrors != (int)failure.PolicyErrors)
        {
            return false;
        }

        try
        {
            var trustedHash = Convert.FromHexString(trust.CertificateSha256 ?? string.Empty);
            var presentedHash = Convert.FromHexString(failure.CertificateSha256 ?? string.Empty);
            return trustedHash.Length > 0 &&
                   trustedHash.Length == presentedHash.Length &&
                   CryptographicOperations.FixedTimeEquals(trustedHash, presentedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void LogRejection(MailServerCertificateFailure failure, bool hadTrust)
    {
        var endpoint = $"{failure.Protocol}|{failure.Host}|{failure.Port}";
        var signature = $"{failure.CertificateSha256}|{failure.ChainStatusFlags}|{(int)failure.PolicyErrors}|{hadTrust}";

        if (LoggedRejections.TryGetValue(endpoint, out var previous) && string.Equals(previous, signature, StringComparison.Ordinal))
            return;

        LoggedRejections[endpoint] = signature;

        Logger.Warning(
            "Rejected the {Protocol} certificate of {Host}:{Port}: tier {Tier}, policy errors {PolicyErrors}, chain status {ChainStatusFlags} ({ChainStatusDetails}), SHA-256 {Sha256}, stored trust present {HadTrust}",
            failure.Protocol,
            failure.Host,
            failure.Port,
            failure.Tier,
            failure.PolicyErrors,
            (X509ChainStatusFlags)failure.ChainStatusFlags,
            failure.ChainStatusDetails,
            failure.CertificateSha256,
            hadTrust);
    }

    private static string GetSubjectAlternativeNames(X509Certificate2? certificate)
        => certificate?.Extensions
            .OfType<X509Extension>()
            .FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17")?
            .Format(false) ?? string.Empty;

    private static string NormalizeHost(string? host) => host?.Trim().ToLowerInvariant() ?? string.Empty;
}
