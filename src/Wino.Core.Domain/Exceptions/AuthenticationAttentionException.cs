using System;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// Thrown when IAuthenticator requires user interaction to fix authentication issues.
/// It can be expired and can't restorable token, or some stuff that requires re-authentication.
/// </summary>
public class AuthenticationAttentionException : Exception
{
    public AuthenticationAttentionException(MailAccount account)
    {
        Account = account;
    }

    /// <summary>Keeps the reason, so whoever reports the failure can say what was rejected.</summary>
    public AuthenticationAttentionException(MailAccount account, string message, Exception innerException)
        : base(message, innerException)
    {
        Account = account;
    }

    public MailAccount Account { get; }
}
