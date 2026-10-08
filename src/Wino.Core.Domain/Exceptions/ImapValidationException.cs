using System;

namespace Wino.Core.Domain.Exceptions;

public class ImapValidationException : Exception
{
    public string ProtocolLog { get; }

    public ImapValidationException(string message, string protocolLog, Exception innerException = null)
        : base(message, innerException)
    {
        ProtocolLog = protocolLog ?? string.Empty;
    }
}
