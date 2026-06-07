using System;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Raised when an Exchange OAuth account cannot obtain an access token without user interaction
/// (no refresh token yet, or the refresh token was rejected/expired). The message intentionally
/// contains the word "authentication" so it routes through <c>ExchangeAuthenticationFailedHandler</c>,
/// flagging the account for re-auth rather than silently downgrading to NTLM.
/// </summary>
public sealed class ExchangeInteractiveSignInRequiredException : Exception
{
    public ExchangeInteractiveSignInRequiredException(string message) : base(message) { }

    public ExchangeInteractiveSignInRequiredException(string message, Exception innerException) : base(message, innerException) { }
}
