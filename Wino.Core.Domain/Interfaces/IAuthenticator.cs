using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAuthenticator
    {
        /// <summary>
        /// Type of the provider.
        /// </summary>
        MailProviderType ProviderType { get; }

        /// <summary>
        /// Gets the token from the cache if exists.
        /// If the token is expired, tries to refresh.
        /// This can throw AuthenticationAttentionException if silent refresh fails.
        /// </summary>
        /// <param name="account">Account to get token for.</param>
        /// <returns>Valid token info to be used in integrators.</returns>
        Task<TokenInformation> GetTokenAsync(MailAccount account);

        /// <summary>
        /// Initial creation of token. Requires user interaction.
        /// This will cache the token but still returns for account creation
        /// since account address is required.
        /// </summary>
        /// <returns>Freshly created TokenInformation..</returns>
        Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken);

        /// <summary>
        /// ClientId in case of needed for authorization/authentication.
        /// </summary>
        string ClientId { get; }
    }
}
