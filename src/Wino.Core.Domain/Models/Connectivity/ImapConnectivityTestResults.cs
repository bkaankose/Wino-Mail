using System;
using System.Linq;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Connectivity;

/// <summary>
/// Contains validation of the IMAP server connectivity during account setup.
/// </summary>
public class ImapConnectivityTestResults
{
    [JsonConstructor]
    protected ImapConnectivityTestResults() { }

    public bool IsSuccess { get; set; }

    public bool IsCertificateUIRequired { get; set; }

    public string FailedReason { get; set; }
    public string ProtocolLog { get; set; }
    public MailServerCertificateFailure CertificateFailure { get; set; }
    public static ImapConnectivityTestResults Success() => new ImapConnectivityTestResults() { IsSuccess = true };
    public static ImapConnectivityTestResults Failure(Exception ex)
    {
        var validationException = ex.GetInnerExceptions().OfType<ImapValidationException>().FirstOrDefault();

        return new ImapConnectivityTestResults()
        {
            FailedReason = validationException?.Message
                ?? string.Join(Environment.NewLine, ex.GetInnerExceptions().Select(e => e.Message)),
            ProtocolLog = validationException?.ProtocolLog
        };
    }

    public static ImapConnectivityTestResults CertificateUIRequired(string issuer,
        string expirationString,
        string validFromString)
    {
        return new ImapConnectivityTestResults()
        {
            IsSuccess = false,
            IsCertificateUIRequired = true,
            CertificateIssuer = issuer,
            CertificateExpirationDateString = expirationString,
            CertificateValidFromDateString = validFromString
        };
    }

    public static ImapConnectivityTestResults CertificateUIRequired(MailServerCertificateFailure failure)
    {
        return new ImapConnectivityTestResults
        {
            IsSuccess = false,
            IsCertificateUIRequired = true,
            CertificateFailure = failure,
            CertificateIssuer = failure?.Issuer,
            CertificateExpirationDateString = failure?.ValidToUtc.ToString("u"),
            CertificateValidFromDateString = failure?.ValidFromUtc.ToString("u")
        };
    }

    public string CertificateIssuer { get; set; }
    public string CertificateValidFromDateString { get; set; }
    public string CertificateExpirationDateString { get; set; }
}
