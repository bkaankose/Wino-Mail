using System;
using System.Linq;
using System.Text.Json.Serialization;
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
    public static ImapConnectivityTestResults Success() => new ImapConnectivityTestResults() { IsSuccess = true };
    public static ImapConnectivityTestResults Failure(Exception ex) => new ImapConnectivityTestResults()
    {
        FailedReason = string.Join(Environment.NewLine, ex.GetInnerExceptions().Select(e => e.Message))
    };

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

    public string CertificateIssuer { get; set; }
    public string CertificateValidFromDateString { get; set; }
    public string CertificateExpirationDateString { get; set; }
}
