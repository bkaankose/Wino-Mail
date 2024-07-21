using System;
using Wino.Domain.Entities;

namespace Wino.Domain.Exceptions
{
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

        public MailAccount Account { get; }
    }
}
