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
    private readonly INativeAppService _nativeAppService;
    private readonly IApplicationConfiguration _applicationConfiguration;

    public OutlookAuthenticator(INativeAppService nativeAppService,
                                IApplicationConfiguration applicationConfiguration,
                                IAuthenticatorConfig authenticatorConfig) : base(authenticatorConfig)
    {
        _nativeAppService = nativeAppService;
        _applicationConfiguration = applicationConfiguration;

        var authenticationRedirectUri = nativeAppService.GetWebAuthenticationBrokerUri();

        var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Wino Mail",
            ListOperatingSystemAccounts = true,
        };

        PublicClientApplicationBuilder outlookAppBuilder = null;

        // Being created from an app notification.
        // This is where we avoid all interactive shit for authentication.
        if (nativeAppService.GetCoreWindowHwnd == null)
        {
            outlookAppBuilder = PublicClientApplicationBuilder.Create(AuthenticatorConfig.OutlookAuthenticatorClientId)
                .WithDefaultRedirectUri()
                .WithBroker(options)
                .WithAuthority(Authority);
        }
        else
        {
            outlookAppBuilder = PublicClientApplicationBuilder.Create(AuthenticatorConfig.OutlookAuthenticatorClientId)
                .WithBroker(options)
                .WithParentActivityOrWindow(_nativeAppService.GetCoreWindowHwnd)
                .WithDefaultRedirectUri()
                .WithAuthority(Authority);
        }

        _publicClientApplication = outlookAppBuilder.Build();
    }

    private string[] GetScope(MailAccount account)
        => AuthenticatorConfig.GetOutlookScope(
            account?.IsMailAccessGranted != false,
            account?.IsCalendarAccessGranted == true);

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

        var storedAccount = (await _publicClientApplication.GetAccountsAsync()).FirstOrDefault(
            a => string.Equals(a.Username?.Trim(), account.Address?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (storedAccount == null)
            return await GenerateTokenInformationAsync(account);

        try
        {
            var authResult = await _publicClientApplication.AcquireTokenSilent(GetScope(account), storedAccount).ExecuteAsync();

            return new TokenInformationEx(authResult.AccessToken, authResult.Account.Username);
        }
        catch (MsalUiRequiredException)
        {
            // Somehow MSAL is not able to refresh the token silently.
            // Force interactive login which will include calendar scopes.
            // The calling code should update account.IsCalendarAccessGranted = true after successful authentication.

            return await GenerateTokenInformationAsync(account);
        }
    }

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
    {
        try
        {
            await EnsureTokenCacheAttachedAsync();

            // Interactive authentication required but window doesn't exist.
            // This can happen when being called from a notification background task and the token is expired.
            // Force account attention;

            if (_nativeAppService.GetCoreWindowHwnd == null) throw new AuthenticationAttentionException(account);

            AuthenticationResult authResult = await _publicClientApplication
                .AcquireTokenInteractive(GetScope(account))
                .ExecuteAsync();

            // Microsoft 365 work/school tenants can use a sign-in UPN that differs from
            // the mailbox primary SMTP address, so interactive reauth must not reject them.

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
