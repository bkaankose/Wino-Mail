using System;

namespace MailCal.Core.Domain.Exceptions;

public class ImapValidationException : Exception
{
    public string ProtocolLog { get; }

    public ImapValidationException(string message, string protocolLog, Exception innerException = null)
        : base(message, innerException)
    {
        ProtocolLog = protocolLog ?? string.Empty;
    }
}
