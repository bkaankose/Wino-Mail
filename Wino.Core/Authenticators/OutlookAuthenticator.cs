using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class OutlookAuthenticator : BaseAuthenticator, IOutlookAuthenticator
    {
        private const string TokenCacheFileName = "OutlookCache.bin";
        private bool isTokenCacheAttached = false;

        // Outlook
        private const string Authority = "https://login.microsoftonline.com/common";

        public string ClientId { get; } = "b19c2035-d740-49ff-b297-de6ec561b208";

        private readonly string[] MailScope = ["email", "mail.readwrite", "offline_access", "mail.send"];

        public override MailProviderType ProviderType => MailProviderType.Outlook;

        private readonly IPublicClientApplication _publicClientApplication;
        private readonly IApplicationConfiguration _applicationConfiguration;

        public OutlookAuthenticator(ITokenService tokenService,
                                    INativeAppService nativeAppService,
                                    IApplicationConfiguration applicationConfiguration) : base(tokenService)
        {
            _applicationConfiguration = applicationConfiguration;

            var authenticationRedirectUri = nativeAppService.GetWebAuthenticationBrokerUri();

            var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Wino Mail"
            };

            var outlookAppBuilder = PublicClientApplicationBuilder.Create(ClientId)
                .WithParentActivityOrWindow(nativeAppService.GetCoreWindowHwnd)
                .WithBroker(options)
                .WithDefaultRedirectUri()
                .WithAuthority(Authority);

            _publicClientApplication = outlookAppBuilder.Build();
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
            if (!isTokenCacheAttached)
            {
                var storageProperties = new StorageCreationPropertiesBuilder(TokenCacheFileName, _applicationConfiguration.PublisherSharedFolderPath).Build();
                var msalcachehelper = await MsalCacheHelper.CreateAsync(storageProperties);
                msalcachehelper.RegisterCache(_publicClientApplication.UserTokenCache);

                isTokenCacheAttached = true;
            }

            var cachedToken = await TokenService.GetTokenInformationAsync(account.Id)
                ?? throw new AuthenticationAttentionException(account);

            // We have token but it's expired.
            // Silently refresh the token and save new token.

            if (cachedToken.IsExpired)
            {
                var accs = await _publicClientApplication.GetAccountsAsync();
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
