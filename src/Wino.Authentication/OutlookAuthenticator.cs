using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

public class OutlookAuthenticator : BaseAuthenticator, IOutlookAuthenticator, ISubstrateTaskTokenProvider
{
    // Exchange Online, not Graph. MSAL issues one token per resource, so this can never be
    // folded into GetScope and has to be acquired on its own.
    private static readonly string[] SubstrateTaskScopes = ["https://outlook.office.com/Tasks.ReadWrite"];
    private static readonly HttpClient GraphProfileHttpClient = new();
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

    private string[] GetScope(MailAccount account, IReadOnlyCollection<ProviderFeature> features = null)
        => AuthenticatorConfig.GetOutlookScopes(ProviderAuthorizationRequest.ForAccount(account, features));

    private async Task EnsureTokenCacheAttachedAsync()
    {
        if (!isTokenCacheAttached)
        {
            var tokenCachePath = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(_applicationConfiguration);
            var tokenCacheDirectory = Path.GetDirectoryName(tokenCachePath)
                ?? throw new InvalidOperationException("The Outlook token cache path has no parent directory.");
            Directory.CreateDirectory(tokenCacheDirectory);

            var storageProperties = new StorageCreationPropertiesBuilder(
                Path.GetFileName(tokenCachePath),
                tokenCacheDirectory).Build();
            var msalcachehelper = await MsalCacheHelper.CreateAsync(storageProperties);
            msalcachehelper.RegisterCache(_publicClientApplication.UserTokenCache);

            isTokenCacheAttached = true;
        }
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        await EnsureTokenCacheAttachedAsync();

        var cachedTokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: false)
            .ConfigureAwait(false);

        if (cachedTokenInfo == null)
        {
            cachedTokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: true)
                .ConfigureAwait(false);
        }

        if (cachedTokenInfo != null)
        {
            ApplyTokenInformation(account, cachedTokenInfo);
            return cachedTokenInfo;
        }

        throw new AuthenticationAttentionException(account);
    }

    public async Task<TokenInformationEx> RefreshTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

        var tokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: true)
            .ConfigureAwait(false);

        if (tokenInfo == null)
        {
            throw new AuthenticationAttentionException(account);
        }

        ApplyTokenInformation(account, tokenInfo);
        return tokenInfo;
    }

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requestedFeatures = null)
    {
        try
        {
            await EnsureTokenCacheAttachedAsync();

            // Interactive authentication required but window doesn't exist.
            // This can happen when being called from a notification background task and the token is expired.
            // Force account attention;

            if (_nativeAppService.GetCoreWindowHwnd == null) throw new AuthenticationAttentionException(account);

            var interactiveBuilder = _publicClientApplication.AcquireTokenInteractive(GetScope(account, requestedFeatures));
            var loginHint = GetAuthenticationAddress(account);

            if (!string.IsNullOrWhiteSpace(loginHint))
                interactiveBuilder = interactiveBuilder.WithLoginHint(loginHint);

            AuthenticationResult authResult = await interactiveBuilder.ExecuteAsync();

            // Microsoft 365 work/school tenants can use a sign-in UPN that differs from
            // the mailbox primary SMTP address, so interactive reauth must not reject them.

            var mailboxAddress = await ResolveMailboxAddressAsync(authResult.AccessToken, authResult.Account.Username)
                .ConfigureAwait(false);

            return new TokenInformationEx(authResult.AccessToken, mailboxAddress, authResult.Account.Username);
        }
        catch (MsalClientException msalClientException)
        {
            if (msalClientException.ErrorCode == "authentication_canceled" || msalClientException.ErrorCode == "access_denied")
                throw new AccountSetupCanceledException();

            throw;
        }

        throw new AuthenticationException(Translator.Exception_UnknowErrorDuringAuthentication, new Exception(Translator.Exception_TokenGenerationFailed));
    }

    /// <summary>
    /// Best-effort token for the To Do substrate API. Returns null whenever the account has not
    /// consented to the Exchange Online resource, or the broker cannot serve it silently, so a
    /// missing consent degrades group discovery instead of breaking task synchronization.
    /// Deliberately never throws <see cref="AuthenticationAttentionException"/>.
    /// </summary>
    public async Task<string> GetSubstrateTaskTokenAsync(MailAccount account)
    {
        if (account is null)
            return null;

        try
        {
            await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

            var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().ConfigureAwait(false)).ToList();
            var storedAccount = FindStoredAccount(cachedAccounts, account);

            if (storedAccount is null)
                return null;

            var authResult = await _publicClientApplication
                .AcquireTokenSilent(SubstrateTaskScopes, storedAccount)
                .ExecuteAsync()
                .ConfigureAwait(false);

            return authResult.AccessToken;
        }
        catch (MsalException)
        {
            return null;
        }
    }

    public async Task EnsureSubstrateTaskConsentAsync(MailAccount account)
    {
        if (account is null)
            throw new ArgumentNullException(nameof(account));

        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);
        var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var storedAccount = FindStoredAccount(cachedAccounts, account);
        var builder = _publicClientApplication.AcquireTokenInteractive(SubstrateTaskScopes);
        if (storedAccount is not null)
            builder = builder.WithAccount(storedAccount);
        else
        {
            var loginHint = GetAuthenticationAddress(account);
            if (!string.IsNullOrWhiteSpace(loginHint))
                builder = builder.WithLoginHint(loginHint);
        }

        await builder.ExecuteAsync().ConfigureAwait(false);
    }

    public async Task DeleteTokenInformationAsync(MailAccount account)
    {
        await EnsureTokenCacheAttachedAsync().ConfigureAwait(false);

        if (account == null)
            return;

        var authenticationAddress = string.IsNullOrWhiteSpace(account.AuthenticationAddress)
            ? account.Address
            : account.AuthenticationAddress;

        var storedAccount = (await _publicClientApplication.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault(
            a => string.Equals(a.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (storedAccount != null)
        {
            await _publicClientApplication.RemoveAsync(storedAccount).ConfigureAwait(false);
        }
    }

    private static async Task<string> ResolveMailboxAddressAsync(string accessToken, string fallbackAddress)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await GraphProfileHttpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return fallbackAddress;

            await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(responseStream).ConfigureAwait(false);

            var root = document.RootElement;
            var mail = GetStringProperty(root, "mail");

            if (!string.IsNullOrWhiteSpace(mail))
                return mail;

            var userPrincipalName = GetStringProperty(root, "userPrincipalName");
            return string.IsNullOrWhiteSpace(userPrincipalName) ? fallbackAddress : userPrincipalName;
        }
        catch
        {
            return fallbackAddress;
        }
    }

    private static string GetStringProperty(JsonElement jsonElement, string propertyName)
        => jsonElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task<TokenInformationEx> TryGetCachedTokenInformationSafelyAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures,
        bool forceRefresh)
    {
        try
        {
            return await TryGetCachedTokenInformationAsync(account, requiredFeatures, forceRefresh).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (MsalClientException)
        {
            return null;
        }
    }

    private async Task<TokenInformationEx> TryGetCachedTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures,
        bool forceRefresh)
    {
        var scopes = GetScope(account, requiredFeatures);
        var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var storedAccount = FindStoredAccount(cachedAccounts, account);

        if (storedAccount != null)
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, storedAccount)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync()
                .ConfigureAwait(false);

            return new TokenInformationEx(authResult.AccessToken, account?.Address, authResult.Account.Username);
        }

        foreach (var cachedAccount in cachedAccounts)
        {
            var tokenInfo = await TryGetMatchingTokenInformationAsync(account, scopes, cachedAccount, forceRefresh)
                .ConfigureAwait(false);

            if (tokenInfo != null)
                return tokenInfo;
        }

        return await TryGetMatchingTokenInformationAsync(
            account,
            scopes,
            PublicClientApplication.OperatingSystemAccount,
            forceRefresh).ConfigureAwait(false);
    }

    private async Task<TokenInformationEx> TryGetMatchingTokenInformationAsync(
        MailAccount account,
        IEnumerable<string> scopes,
        IAccount cachedAccount,
        bool forceRefresh)
    {
        try
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, cachedAccount)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync()
                .ConfigureAwait(false);

            return await GetValidatedTokenInformationAsync(account, authResult).ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (MsalClientException)
        {
            return null;
        }
    }

    private async Task<TokenInformationEx> GetValidatedTokenInformationAsync(MailAccount account, AuthenticationResult authResult)
    {
        if (account == null)
            return new TokenInformationEx(authResult.AccessToken, authResult.Account.Username, authResult.Account.Username);

        var authenticationAddress = GetAuthenticationAddress(account);

        if (AddressesMatch(authResult.Account.Username, authenticationAddress) ||
            AddressesMatch(authResult.Account.Username, account.Address))
        {
            return new TokenInformationEx(authResult.AccessToken, account.Address, authResult.Account.Username);
        }

        var mailboxAddress = await ResolveMailboxAddressAsync(authResult.AccessToken, authResult.Account.Username)
            .ConfigureAwait(false);

        return AddressesMatch(mailboxAddress, account.Address)
            ? new TokenInformationEx(authResult.AccessToken, mailboxAddress, authResult.Account.Username)
            : null;
    }

    private static IAccount FindStoredAccount(IEnumerable<IAccount> cachedAccounts, MailAccount account)
    {
        var authenticationAddress = GetAuthenticationAddress(account);

        return cachedAccounts.FirstOrDefault(a =>
            AddressesMatch(a.Username, authenticationAddress) ||
            AddressesMatch(a.Username, account?.Address));
    }

    private static string GetAuthenticationAddress(MailAccount account)
        => string.IsNullOrWhiteSpace(account?.AuthenticationAddress)
            ? account?.Address
            : account.AuthenticationAddress;

    private static bool AddressesMatch(string firstAddress, string secondAddress)
        => !string.IsNullOrWhiteSpace(firstAddress) &&
           !string.IsNullOrWhiteSpace(secondAddress) &&
           string.Equals(firstAddress.Trim(), secondAddress.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ApplyTokenInformation(MailAccount account, TokenInformationEx tokenInformation)
    {
        if (account == null || tokenInformation == null)
            return;

        if (!string.IsNullOrWhiteSpace(tokenInformation.AccountAddress))
            account.Address = tokenInformation.AccountAddress;

        if (!string.IsNullOrWhiteSpace(tokenInformation.AuthenticationAddress))
            account.AuthenticationAddress = tokenInformation.AuthenticationAddress;
    }
}
