using System;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Exceptions;

public sealed class MailServerCertificateException : Exception
{
    public MailServerCertificateException(MailServerCertificateFailure failure)
        : base($"The {failure?.Protocol} certificate for {failure?.Host}:{failure?.Port} could not be validated.")
    {
        Failure = failure;
    }

    public MailServerCertificateFailure Failure { get; }
}
