using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Authentication.Exchange;

/// <summary>
/// Produces credentials for an on-premises Exchange account: OAuth bearer tokens minted from the stored
/// refresh token, or the stored password as NTLM network credentials. Also the provider's
/// <see cref="IAuthenticator"/> so the authentication provider can hand it out like Outlook and Gmail.
/// </summary>
public interface IExchangeAuthenticator : IAuthenticator
{
    Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account);

    /// <summary>
    /// Returns an OAuth bearer access token for the account, or null for password (NTLM) accounts. Used to
    /// authenticate the MAPI/HTTP transport and hand-rolled HTTP calls (Autodiscover) that bypass the
    /// EWS library.
    /// </summary>
    Task<string> TryGetBearerTokenAsync(MailAccount account);
}
