using System;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>Raised when Exchange OAuth requires a fresh interactive sign-in.</summary>
public sealed class ExchangeInteractiveSignInRequiredException : Exception
{
    public ExchangeInteractiveSignInRequiredException(string message) : base(message) { }

    public ExchangeInteractiveSignInRequiredException(string message, Exception innerException) : base(message, innerException) { }
}
