using System;

namespace Wino.Core.Domain.Exceptions;

public class ImapValidationException : Exception
{
    public string ProtocolLog { get; }

    /// <summary>The outgoing (SMTP) server refused; the incoming server answered.</summary>
    public bool IsOutgoingServer { get; }

    public ImapValidationException(string message, string protocolLog, Exception innerException = null, bool isOutgoingServer = false)
        : base(message, innerException)
    {
        ProtocolLog = protocolLog ?? string.Empty;
        IsOutgoingServer = isOutgoingServer;
    }
}
