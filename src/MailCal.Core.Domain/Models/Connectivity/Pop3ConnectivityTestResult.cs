using System;
using MailCal.Core.Domain.Exceptions;

namespace MailCal.Core.Domain.Models.Connectivity;

public sealed record Pop3ConnectivityTestResult(
    bool IsSuccess,
    bool SupportsUidl,
    string FailedReason = null,
    string ProtocolLog = null,
    MailServerCertificateFailure CertificateFailure = null)
{
    public bool IsCertificateUIRequired => CertificateFailure != null;
    public static Pop3ConnectivityTestResult Success() => new(true, true);

    public static Pop3ConnectivityTestResult Failure(Exception exception)
    {
        var validationException = exception as Pop3ValidationException
                                  ?? exception?.GetBaseException() as Pop3ValidationException;
        return new(false, false,
            validationException?.Message ?? exception?.GetBaseException().Message,
            validationException?.ProtocolLog);
    }
}
