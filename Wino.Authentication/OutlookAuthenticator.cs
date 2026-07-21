using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
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
    private static readonly HttpClient GraphProfileHttpClient = new();
    private bool isTokenCacheAttached = false;

    // Outlook
    private const string Authority = "https://login.microsoftonline.com/common";

    public override MailProviderType ProviderType => MailProviderType.Outlook;

    private readonly IPublicClientApplication _publicClientApplication;
    private readonly INativeAppService _nativeAppService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly SemaphoreSlim _interactiveAuthenticationLock = new(1, 1);
    private nint _interactiveParentWindow;

    public OutlookAuthenticator(INativeAppService nativeAppService,
                                IApplicationConfiguration applicationConfiguration,
                                IAuthenticatorConfig authenticatorConfig) : base(authenticatorConfig)
    {
        _nativeAppService = nativeAppService;
        _applicationConfiguration = applicationConfiguration;

        var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = "Wino Mail",
            ListOperatingSystemAccounts = true,
        };

        _publicClientApplication = PublicClientApplicationBuilder
            .Create(AuthenticatorConfig.OutlookAuthenticatorClientId)
            .WithBroker(options)
            .WithParentActivityOrWindow(() => _interactiveParentWindow)
            .WithDefaultRedirectUri()
            .WithAuthority(Authority)
            .Build();
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

        try
        {
            var cachedTokenInfo = await TryGetCachedTokenInformationAsync(account).ConfigureAwait(false);

            if (cachedTokenInfo != null)
            {
                ApplyTokenInformation(account, cachedTokenInfo);
                return cachedTokenInfo;
            }
        }
        catch (MsalUiRequiredException)
        {
            // Somehow MSAL is not able to refresh the token silently.
        }

        // Force interactive login which will include calendar scopes.
        // The calling code should update account.IsCalendarAccessGranted = true after successful authentication.

        var generatedTokenInfo = await GenerateTokenInformationAsync(account).ConfigureAwait(false);
        ApplyTokenInformation(account, generatedTokenInfo);

        return generatedTokenInfo;
    }

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
    {
        var parentWindowHandle = _nativeAppService.GetCoreWindowHwnd?.Invoke() ?? nint.Zero;
        if (parentWindowHandle == nint.Zero)
        {
            throw new AuthenticationAttentionException(account);
        }

        return await GenerateTokenInformationAsync(account, parentWindowHandle, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(
        MailAccount account,
        nint parentWindowHandle,
        CancellationToken cancellationToken)
    {
        if (parentWindowHandle == nint.Zero)
        {
            throw new AuthenticationAttentionException(account);
        }

        await _interactiveAuthenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTokenCacheAttachedAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                _interactiveParentWindow = attempt == 0
                    ? parentWindowHandle
                    : _nativeAppService.GetCoreWindowHwnd?.Invoke() ?? nint.Zero;

                if (_interactiveParentWindow == nint.Zero)
                {
                    throw new AuthenticationAttentionException(account);
                }

                try
                {
                    var interactiveBuilder = _publicClientApplication.AcquireTokenInteractive(GetScope(account));
                    var loginHint = GetAuthenticationAddress(account);

                    if (!string.IsNullOrWhiteSpace(loginHint))
                        interactiveBuilder = interactiveBuilder.WithLoginHint(loginHint);

                    AuthenticationResult authResult = await interactiveBuilder.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    // Microsoft 365 work/school tenants can use a sign-in UPN that differs from
                    // the mailbox primary SMTP address, so interactive reauth must not reject them.
                    var mailboxAddress = await ResolveMailboxAddressAsync(authResult.AccessToken, authResult.Account.Username)
                        .ConfigureAwait(false);

                    return new TokenInformationEx(authResult.AccessToken, mailboxAddress, authResult.Account.Username);
                }
                catch (MsalClientException exception) when (attempt == 0 && IsParentWindowFailure(exception))
                {
                    // The UWP CoreWindow may have been recreated between validation and WAM
                    // startup. Request a fresh HWND once and never retain either handle.
                }
            }

            throw new InvalidOperationException("Interactive Outlook authentication did not produce a token.");
        }
        catch (MsalClientException msalClientException)
        {
            if (msalClientException.ErrorCode == "authentication_canceled" || msalClientException.ErrorCode == "access_denied")
                throw new AccountSetupCanceledException();

            throw;
        }
        finally
        {
            _interactiveParentWindow = nint.Zero;
            _interactiveAuthenticationLock.Release();
        }
    }

    private static bool IsParentWindowFailure(MsalClientException exception) =>
        exception.ErrorCode.Contains("window", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("window handle", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("parent window", StringComparison.OrdinalIgnoreCase);

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

    private async Task<TokenInformationEx> TryGetCachedTokenInformationAsync(MailAccount account)
    {
        var scopes = GetScope(account);
        var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var storedAccount = FindStoredAccount(cachedAccounts, account);

        if (storedAccount != null)
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, storedAccount)
                .ExecuteAsync()
                .ConfigureAwait(false);

            return new TokenInformationEx(authResult.AccessToken, account?.Address, authResult.Account.Username);
        }

        foreach (var cachedAccount in cachedAccounts)
        {
            var tokenInfo = await TryGetMatchingTokenInformationAsync(account, scopes, cachedAccount).ConfigureAwait(false);

            if (tokenInfo != null)
                return tokenInfo;
        }

        return await TryGetMatchingTokenInformationAsync(account, scopes, PublicClientApplication.OperatingSystemAccount).ConfigureAwait(false);
    }

    private async Task<TokenInformationEx> TryGetMatchingTokenInformationAsync(MailAccount account, IEnumerable<string> scopes, IAccount cachedAccount)
    {
        try
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, cachedAccount)
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
