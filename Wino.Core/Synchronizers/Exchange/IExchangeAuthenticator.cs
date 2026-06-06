using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Produces EWS credentials for an on-premises Exchange account.
/// Implementations are swappable behind this seam: NTLM/Negotiate (spike) and
/// OAuth bearer via ExSTS (production, Phase 3). The synchronizer only ever sees
/// an <see cref="ExchangeCredentials"/>, so the auth mechanism is interchangeable.
/// </summary>
public interface IExchangeAuthenticator
{
    MailProviderType ProviderType { get; }

    /// <summary>
    /// Returns the credentials used to build the account's <see cref="ExchangeService"/>.
    /// </summary>
    Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account);
}
