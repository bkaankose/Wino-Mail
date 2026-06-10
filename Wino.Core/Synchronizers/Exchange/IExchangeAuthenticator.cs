using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>Produces EWS credentials for an on-premises Exchange account.</summary>
public interface IExchangeAuthenticator
{
    MailProviderType ProviderType { get; }

    Task<ExchangeCredentials> GetCredentialsAsync(MailAccount account);
}
