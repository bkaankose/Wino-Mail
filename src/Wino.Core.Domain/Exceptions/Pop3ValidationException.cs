using System;

namespace Wino.Core.Domain.Exceptions;

public sealed class Pop3ValidationException : Exception
{
    public string ProtocolLog { get; }

    public Pop3ValidationException(string message, string protocolLog, Exception innerException = null)
        : base(message, innerException)
    {
        ProtocolLog = protocolLog ?? string.Empty;
    }
}
