using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticator
{
    /// <summary>
    /// Type of the provider.
    /// </summary>
    MailProviderType ProviderType { get; }

    Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account);

    Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account);

    ///// <summary>
    ///// Gets the token for the given account from the cache.
    ///// Forces interactive login if the token is not found.
    ///// </summary>
    ///// <param name="account">Account to get access token for.</param>
    ///// <returns>Access token</returns>
    //Task<string> GetTokenAsync(MailAccount account);

    ///// <summary>
    ///// Forces an interactive login to get the token for the given account.
    ///// </summary>
    ///// <param name="account">Account to get access token for.</param>
    ///// <returns>Access token</returns>
    //// Task<string> GenerateTokenAsync(MailAccount account);

    ///// <summary>
    ///// ClientId in case of needed for authorization/authentication.
    ///// </summary>
    //string ClientId { get; }
}
