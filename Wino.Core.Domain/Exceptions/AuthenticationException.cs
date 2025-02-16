using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// All exceptions related to authentication.
/// </summary>
public class AuthenticationException : Exception
{
    public AuthenticationException(string message) : base(message)
    {
    }

    public AuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
