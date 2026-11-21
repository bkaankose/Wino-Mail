using System;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Domain.Models.Connectivity;

public sealed record Pop3ConnectivityTestResult(
    bool IsSuccess,
    bool SupportsUidl,
    string FailedReason = null,
    string ProtocolLog = null,
    MailServerCertificateFailure CertificateFailure = null)
{
    public bool IsCertificateUIRequired => CertificateFailure != null;

    /// <summary>The outgoing (SMTP) server failed after the POP3 server answered.</summary>
    public bool IsOutgoingServerFailure { get; init; }
    public static Pop3ConnectivityTestResult Success() => new(true, true);

    public static Pop3ConnectivityTestResult Failure(Exception exception)
    {
        var validationException = exception as Pop3ValidationException
                                  ?? exception?.GetBaseException() as Pop3ValidationException;
        return new(false, false,
            validationException?.Message ?? exception?.GetBaseException().Message,
            validationException?.ProtocolLog)
        {
            IsOutgoingServerFailure = validationException?.IsOutgoingServer == true
        };
    }
}
