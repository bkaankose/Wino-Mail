using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Exceptions;

public class ImapClientPoolException : Exception
{
    public ImapClientPoolException()
    {
    }

    public ImapClientPoolException(string message, CustomServerInformation customServerInformation) : base(message)
    {
        CustomServerInformation = customServerInformation;
    }

    public ImapClientPoolException(string message) : base(message)
    {
    }

    public ImapClientPoolException(Exception innerException) : base(innerException.Message, innerException)
    {
    }

    public CustomServerInformation CustomServerInformation { get; }
}
