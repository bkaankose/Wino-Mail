#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IOutlookAuthenticator : IAuthenticator { }

public interface IGmailAuthenticator : IAuthenticator
{
    /// <summary>
    /// A newly issued Google ID token for the account: it proves the address to the push relay without giving it mailbox
    /// access. Null when this sign-in cannot produce one (the grant lacks OpenID or the platform issues access tokens only).
    /// </summary>
    Task<string?> GetIdentityTokenAsync(MailAccount account, CancellationToken cancellationToken = default);
}

public interface IImapAuthenticator : IAuthenticator { }
