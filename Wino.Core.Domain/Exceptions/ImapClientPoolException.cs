using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Exceptions;

public class ImapClientPoolException : Exception
{
    public ImapClientPoolException()
    {
    }

    public ImapClientPoolException(string message, CustomServerInformation customServerInformation, string protocolLog) : base(message)
    {
        CustomServerInformation = customServerInformation;
        ProtocolLog = protocolLog;
    }

    public ImapClientPoolException(string message, string protocolLog) : base(message)
    {
        ProtocolLog = protocolLog;
    }

    public ImapClientPoolException(Exception innerException, string protocolLog) : base(Translator.Exception_ImapClientPoolFailed, innerException)
    {
        ProtocolLog = protocolLog;
    }

    public CustomServerInformation CustomServerInformation { get; }
    public string ProtocolLog { get; }
}
