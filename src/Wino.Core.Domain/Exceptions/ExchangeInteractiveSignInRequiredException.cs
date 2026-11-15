#nullable enable
using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// A modern-auth Exchange account has no usable refresh token, so only an interactive sign-in with the
/// organization's identity provider can restore access. Nothing in the background ever shows that sign-in.
/// </summary>
public sealed class ExchangeInteractiveSignInRequiredException : AuthenticationAttentionException
{
    public ExchangeInteractiveSignInRequiredException(MailAccount account, string message, Exception? innerException = null)
        : base(account, message, innerException)
    {
    }
}
