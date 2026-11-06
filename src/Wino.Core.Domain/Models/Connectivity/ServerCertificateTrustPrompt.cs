using System;
using System.Collections.Generic;
using System.Globalization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Connectivity;

public sealed class ServerCertificateFact
{
    public ServerCertificateFact(string label, string value)
    {
        Label = label;
        Value = string.IsNullOrWhiteSpace(value) ? "—" : value;
    }

    public string Label { get; }
    public string Value { get; }
}

/// <summary>
/// Everything a platform needs to render the server certificate trust prompt. The text is decided here so every
/// head shows the same reasons, warnings and buttons for the same failure.
/// </summary>
public sealed class ServerCertificateTrustPrompt
{
    private ServerCertificateTrustPrompt() { }

    public MailServerCertificateTrustTier Tier { get; private init; }
    public string Title { get; private init; } = string.Empty;
    public string Intro { get; private init; } = string.Empty;
    public string ProblemsHeader { get; private init; } = string.Empty;
    public string[] Problems { get; private init; } = [];
    public ServerCertificateFact[] Facts { get; private init; } = [];
    public string Closing { get; private init; } = string.Empty;
    public string AcknowledgementText { get; private init; } = string.Empty;
    public string AcceptButtonText { get; private init; } = string.Empty;
    public byte[] CertificateRawData { get; private init; } = [];
    public string BlockedMessage { get; private init; } = string.Empty;

    public bool IsDangerous => Tier == MailServerCertificateTrustTier.Dangerous;

    public static ServerCertificateTrustPrompt From(MailServerCertificateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var isDangerous = failure.Tier == MailServerCertificateTrustTier.Dangerous;

        return new ServerCertificateTrustPrompt
        {
            Tier = failure.Tier,
            Title = Translator.IMAPSetupDialog_CertificateTrustTitle,
            Intro = isDangerous
                ? Translator.IMAPSetupDialog_CertificateDangerousWarning
                : Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0,
            ProblemsHeader = Translator.IMAPSetupDialog_CertificateProblems,
            Problems = DescribeProblems(failure),
            Facts = DescribeFacts(failure),
            Closing = Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1,
            AcknowledgementText = isDangerous ? Translator.IMAPSetupDialog_CertificateDangerousAcknowledge : string.Empty,
            AcceptButtonText = isDangerous ? Translator.Buttons_AcceptAnyway : Translator.Buttons_Trust,
            CertificateRawData = failure.CertificateRawData ?? [],
            BlockedMessage = failure.Problems.HasFlag(MailServerCertificateProblem.NoCertificate)
                ? Translator.IMAPSetupDialog_CertificateNotAvailable
                : Translator.IMAPSetupDialog_CertificateCannotBeTrusted
        };
    }

    private static string[] DescribeProblems(MailServerCertificateFailure failure)
    {
        var problems = failure.Problems;
        var lines = new List<string>();

        if (problems.HasFlag(MailServerCertificateProblem.NoCertificate))
            lines.Add(Translator.IMAPSetupDialog_CertificateNotAvailable);
        if (problems.HasFlag(MailServerCertificateProblem.Revoked))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_Revoked);
        if (problems.HasFlag(MailServerCertificateProblem.InvalidSignature))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_InvalidSignature);
        if (problems.HasFlag(MailServerCertificateProblem.HostNameMismatch))
            lines.Add(string.Format(Translator.IMAPSetupDialog_CertificateProblem_HostNameMismatch, failure.Host));
        if (problems.HasFlag(MailServerCertificateProblem.Expired))
            lines.Add(string.Format(Translator.IMAPSetupDialog_CertificateProblem_Expired, FormatDate(failure.ValidToUtc)));
        if (problems.HasFlag(MailServerCertificateProblem.NotYetValid))
            lines.Add(string.Format(Translator.IMAPSetupDialog_CertificateProblem_NotYetValid, FormatDate(failure.ValidFromUtc)));
        if (problems.HasFlag(MailServerCertificateProblem.UntrustedRoot))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_UntrustedRoot);
        if (problems.HasFlag(MailServerCertificateProblem.PartialChain))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_PartialChain);
        if (problems.HasFlag(MailServerCertificateProblem.RevocationUnknown))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_RevocationUnknown);
        if (problems.HasFlag(MailServerCertificateProblem.UnverifiedChain))
            lines.Add(Translator.IMAPSetupDialog_CertificateProblem_UnverifiedChain);
        if (problems.HasFlag(MailServerCertificateProblem.Other))
        {
            var details = string.IsNullOrWhiteSpace(failure.ChainStatusDetails)
                ? failure.PolicyErrors.ToString()
                : failure.ChainStatusDetails.ReplaceLineEndings(" ");
            lines.Add(string.Format(Translator.IMAPSetupDialog_CertificateProblem_Other, details));
        }

        return [.. lines];
    }

    private static ServerCertificateFact[] DescribeFacts(MailServerCertificateFailure failure)
    {
        var protocol = new ServerCertificateFact(Translator.IMAPSetupDialog_CertificateProtocol, ProtocolName(failure.Protocol));
        var endpoint = new ServerCertificateFact(Translator.IMAPSetupDialog_CertificateEndpoint, $"{failure.Host}:{failure.Port}");

        if (failure.Problems.HasFlag(MailServerCertificateProblem.NoCertificate))
            return [protocol, endpoint];

        return
        [
            protocol,
            endpoint,
            new(Translator.IMAPSetupDialog_CertificateSubject, failure.Subject),
            new(Translator.IMAPSetupDialog_CertificateSans, failure.SubjectAlternativeNames),
            new(Translator.IMAPSetupDialog_CertificateIssuer, failure.Issuer),
            new(Translator.IMAPSetupDialog_CertificateValidFrom, failure.ValidFromUtc.ToString("u", CultureInfo.InvariantCulture)),
            new(Translator.IMAPSetupDialog_CertificateValidTo, failure.ValidToUtc.ToString("u", CultureInfo.InvariantCulture)),
            new(Translator.IMAPSetupDialog_CertificateFingerprint, failure.CertificateSha256)
        ];
    }

    private static string ProtocolName(MailServerProtocol protocol) => protocol switch
    {
        MailServerProtocol.Imap => "IMAP",
        MailServerProtocol.Smtp => "SMTP",
        MailServerProtocol.Pop3 => "POP3",
        MailServerProtocol.CalDav => "CalDAV",
        MailServerProtocol.CardDav => "CardDAV",
        _ => protocol.ToString()
    };

    private static string FormatDate(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
