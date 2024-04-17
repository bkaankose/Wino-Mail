using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
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
        /// This will save token into database, but still returns for account creation
        /// since account address is required.
        /// </summary>
        /// <param name="expectedAccountAddress">Token cache might ask for regeneration of token for specific
        /// account address. If one is provided and re-generation native token doesn't belong to this address
        /// token saving to database won't happen.</param>
        /// <returns>Freshly created TokenInformation..</returns>
        Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken);

        /// <summary>
        /// Required for external authorization on launched browser to continue.
        /// Used for Gmail.
        /// </summary>
        /// <param name="authorizationResponseUri">Response's redirect uri.</param>
        void ContinueAuthorization(Uri authorizationResponseUri);

        /// <summary>
        /// For external browser required authentications.
        /// Canceling Gmail authentication dialog etc.
        /// </summary>
        void CancelAuthorization();

        /// <summary>
        /// ClientId in case of needed for authorization/authentication.
        /// </summary>
        string ClientId { get; }
    }
}
