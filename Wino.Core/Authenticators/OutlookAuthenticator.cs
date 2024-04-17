using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class OutlookAuthenticator : BaseAuthenticator, IAuthenticator
    {
        // Outlook
        private const string Authority = "https://login.microsoftonline.com/common";

        public string ClientId { get; } = "b19c2035-d740-49ff-b297-de6ec561b208";

        private readonly string[] MailScope = new string[] { "email", "mail.readwrite", "offline_access", "mail.send" };

        public override MailProviderType ProviderType => MailProviderType.Outlook;

        private readonly IPublicClientApplication _publicClientApplication;

        public OutlookAuthenticator(ITokenService tokenService, INativeAppService nativeAppService) : base(tokenService)
        {
            var authenticationRedirectUri = nativeAppService.GetWebAuthenticationBrokerUri();

            _publicClientApplication = PublicClientApplicationBuilder.Create(ClientId)
                .WithAuthority(Authority)
                .WithRedirectUri(authenticationRedirectUri)
                .Build();
        }

#pragma warning disable S1133 // Deprecated code should be removed
        [Obsolete("Not used for OutlookAuthenticator.")]
#pragma warning restore S1133 // Deprecated code should be removed
        public void ContinueAuthorization(Uri authorizationResponseUri) { }

#pragma warning disable S1133 // Deprecated code should be removed
        [Obsolete("Not used for OutlookAuthenticator.")]
#pragma warning restore S1133 // Deprecated code should be removed
        public void CancelAuthorization() { }

        public async Task<TokenInformation> GetTokenAsync(MailAccount account)
        {
            var cachedToken = await TokenService.GetTokenInformationAsync(account.Id)
                ?? throw new AuthenticationAttentionException(account);

            // We have token but it's expired.
            // Silently refresh the token and save new token.

            if (cachedToken.IsExpired)
            {
                var cachedOutlookAccount = (await _publicClientApplication.GetAccountsAsync()).FirstOrDefault(a => a.Username == account.Address);

                // Again, not expected at all...
                // Force interactive login at this point.

                if (cachedOutlookAccount == null)
                {
                    // What if interactive login info is for different account?

                    return await GenerateTokenAsync(account, true);
                }
                else
                {
                    // Silently refresh token from cache.

                    AuthenticationResult authResult = await _publicClientApplication.AcquireTokenSilent(MailScope, cachedOutlookAccount).ExecuteAsync();

                    // Save refreshed token and return
                    var refreshedTokenInformation = authResult.CreateTokenInformation();

                    await TokenService.SaveTokenInformationAsync(account.Id, refreshedTokenInformation);

                    return refreshedTokenInformation;
                }
            }
            else
                return cachedToken;
        }

        public async Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken)
        {
            try
            {
                var authResult = await _publicClientApplication
                    .AcquireTokenInteractive(MailScope)
                    .ExecuteAsync();

                var tokenInformation = authResult.CreateTokenInformation();

                if (saveToken)
                {
                    await SaveTokenInternalAsync(account, tokenInformation);
                }

                return tokenInformation;
            }
            catch (MsalClientException msalClientException)
            {
                if (msalClientException.ErrorCode == "authentication_canceled" || msalClientException.ErrorCode == "access_denied")
                    throw new AccountSetupCanceledException();

                throw;
            }

            throw new AuthenticationException(Translator.Exception_UnknowErrorDuringAuthentication, new Exception(Translator.Exception_TokenGenerationFailed));
        }
    }
}
