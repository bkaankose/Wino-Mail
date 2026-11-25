using System;

namespace Wino.Core.Domain.Exceptions;

public sealed class Pop3ValidationException : Exception
{
    public string ProtocolLog { get; }

    /// <summary>The outgoing (SMTP) server refused; the POP3 server answered.</summary>
    public bool IsOutgoingServer { get; }

    public Pop3ValidationException(string message, string protocolLog, Exception innerException = null, bool isOutgoingServer = false)
        : base(message, innerException)
    {
        ProtocolLog = protocolLog ?? string.Empty;
        IsOutgoingServer = isOutgoingServer;
    }
}
