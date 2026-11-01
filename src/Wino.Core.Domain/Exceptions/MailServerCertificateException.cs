using System;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Exceptions;

public sealed class MailServerCertificateException : Exception
{
    public MailServerCertificateException(MailServerCertificateFailure failure)
        : this(failure, null)
    {
    }

    public MailServerCertificateException(MailServerCertificateFailure failure, Exception innerException)
        : base($"The {failure?.Protocol} certificate for {failure?.Host}:{failure?.Port} could not be validated.", innerException)
    {
        Failure = failure;
    }

    public MailServerCertificateFailure Failure { get; }
}
