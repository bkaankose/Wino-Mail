using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication;

public class OutlookAuthenticator : BaseAuthenticator, IOutlookAuthenticator
{
    private const string TokenCacheFileName = "OutlookCache.bin";
    private bool isTokenCacheAttached = false;

    // Outlook
    private const string Authority = "https://login.microsoftonline.com/common";

    public override MailProviderType ProviderType => MailProviderType.Outlook;

    private readonly IPublicClientApplication _publicClientApplication;
    private readonly IApplicationConfiguration _applicationConfiguration;

    public OutlookAuthenticator(INativeAppService nativeAppService,
                                IApplicationConfiguration applicationConfiguration,
                                IAuthenticatorConfig authenticatorConfig) : base(authenticatorConfig)
    {
        _applicationConfiguration = applicationConfiguration;

        var authenticationRedirectUri = nativeAppService.GetWebAuthenticationBrokerUri();

        var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Wino Mail",
            ListOperatingSystemAccounts = true,
        };

        var outlookAppBuilder = PublicClientApplicationBuilder.Create(AuthenticatorConfig.OutlookAuthenticatorClientId)
            .WithParentActivityOrWindow(nativeAppService.GetCoreWindowHwnd)
            .WithBroker(options)
            .WithDefaultRedirectUri()
            .WithAuthority(Authority);

        _publicClientApplication = outlookAppBuilder.Build();
    }

    public string[] Scope => AuthenticatorConfig.OutlookScope;

    private async Task EnsureTokenCacheAttachedAsync()
    {
        if (!isTokenCacheAttached)
        {
            var storageProperties = new StorageCreationPropertiesBuilder(TokenCacheFileName, _applicationConfiguration.PublisherSharedFolderPath).Build();
            var msalcachehelper = await MsalCacheHelper.CreateAsync(storageProperties);
            msalcachehelper.RegisterCache(_publicClientApplication.UserTokenCache);

            isTokenCacheAttached = true;
        }
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account)
    {
        await EnsureTokenCacheAttachedAsync();

        var storedAccount = (await _publicClientApplication.GetAccountsAsync()).FirstOrDefault(a => a.Username == account.Address);

        if (storedAccount == null)
            return await GenerateTokenInformationAsync(account);

        try
        {
            var authResult = await _publicClientApplication.AcquireTokenSilent(Scope, storedAccount).ExecuteAsync();

            return new TokenInformationEx(authResult.AccessToken, authResult.Account.Username);
        }
        catch (MsalUiRequiredException)
        {
            // Somehow MSAL is not able to refresh the token silently.
            // Force interactive login.

            return await GenerateTokenInformationAsync(account);
        }
        catch (Exception)
        {
            throw;
        }
    }

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
    {
        try
        {
            await EnsureTokenCacheAttachedAsync();

            var authResult = await _publicClientApplication
                .AcquireTokenInteractive(Scope)
                .ExecuteAsync();

            // If the account is null, it means it's the initial creation of it.
            // If not, make sure the authenticated user address matches the username.
            // When people refresh their token, accounts must match.

            if (account?.Address != null && !account.Address.Equals(authResult.Account.Username, StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthenticationException("Authenticated address does not match with your account address. If you are signing with a Office365, it is not officially supported yet.");
            }

            return new TokenInformationEx(authResult.AccessToken, authResult.Account.Username);
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
