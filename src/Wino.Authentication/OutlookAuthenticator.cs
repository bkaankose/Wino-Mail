using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Serilog;
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

    // Reuse an access token until shortly before it expires.
    private static readonly TimeSpan AccessTokenReuseMargin = TimeSpan.FromMinutes(5);
    private static readonly ILogger Logger = Log.ForContext<OutlookAuthenticator>();

    // MSAL serves a cached access token only when its granted scopes cover every requested scope.
    // Personal Microsoft accounts never grant the *.Shared scopes, so without the broker each
    // AcquireTokenSilent call redeems the refresh token. Concurrent synchronizer requests then
    // trigger Entra loop detection (AADSTS50196) and the account ends in "needs attention".
    // Keep the issued token per account and scope set, and let one caller refresh at a time.
    // The authenticator is transient, so this state is shared by every instance.
    private static readonly ConcurrentDictionary<string, CachedAccessToken> AccessTokens = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessTokenGates = new(StringComparer.Ordinal);

    private sealed record CachedAccessToken(TokenInformationEx TokenInformation, DateTimeOffset ExpiresOn, DateTimeOffset AcquiredOn);
    private sealed record TokenAcquisition(TokenInformationEx TokenInformation, DateTimeOffset ExpiresOn);

    public override MailProviderType ProviderType => MailProviderType.Outlook;

    private readonly IPublicClientApplication _publicClientApplication;
    private readonly IOutlookAuthenticationHost _host;

    public OutlookAuthenticator(IOutlookAuthenticationHost host, IAuthenticatorConfig authenticatorConfig)
        : base(authenticatorConfig)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _publicClientApplication = host.Client;
    }

    private string[] GetScope(MailAccount account, IReadOnlyCollection<ProviderFeature> features = null)
        => AuthenticatorConfig.GetOutlookScopes(ProviderAuthorizationRequest.ForAccount(account, features));

    public async Task<TokenInformationEx> GetTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        await _host.EnsureTokenCacheAttachedAsync(cancellationToken);

        var cachedTokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: false, cancellationToken)
            .ConfigureAwait(false);

        if (cachedTokenInfo == null)
        {
            cachedTokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: true, cancellationToken)
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
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        await _host.EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);

        var tokenInfo = await TryGetCachedTokenInformationSafelyAsync(account, requiredFeatures, forceRefresh: true, cancellationToken)
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
        IReadOnlyCollection<ProviderFeature> requestedFeatures = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await _host.EnsureTokenCacheAttachedAsync(cancellationToken);

            // Interactive authentication required but window doesn't exist.
            // This can happen when being called from a notification background task and the token is expired.
            // Force account attention;

            if (!_host.CanAuthenticateInteractively) throw new AuthenticationAttentionException(account);

            var cachedAccounts = (await _publicClientApplication
                .GetAccountsAsync().WaitAsync(cancellationToken)
                .ConfigureAwait(false))
                .ToList();
            var storedAccount = FindStoredAccount(cachedAccounts, account);

            AuthenticationResult authResult = await _host.AcquireTokenInteractiveAsync(
                GetScope(account, requestedFeatures), storedAccount, GetAuthenticationAddress(account), cancellationToken)
                .ConfigureAwait(false);

            // Microsoft 365 work/school tenants can use a sign-in UPN that differs from
            // the mailbox primary SMTP address, so interactive reauth must not reject them.

            var mailboxAddress = await ResolveMailboxAddressAsync(authResult.AccessToken, authResult.Account.Username, cancellationToken)
                .ConfigureAwait(false);

            ClearCachedAccessTokens(account);

            return new TokenInformationEx(authResult.AccessToken, mailboxAddress, authResult.Account.Username);
        }
        catch (MsalClientException msalClientException)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
    public async Task<string> GetSubstrateTaskTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account is null)
            return null;

        try
        {
            await _host.EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);

            var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var storedAccount = FindStoredAccount(cachedAccounts, account);

            if (storedAccount is null)
                return null;

            var authResult = await _publicClientApplication
                .AcquireTokenSilent(SubstrateTaskScopes, storedAccount)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return authResult.AccessToken;
        }
        catch (MsalException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Logger.Debug("Substrate task token is unavailable. ErrorCode: {ErrorCode}, Classification: {Classification}",
                ex.ErrorCode, (ex as MsalUiRequiredException)?.Classification);

            return null;
        }
    }

    public async Task EnsureSubstrateTaskConsentAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account is null)
            throw new ArgumentNullException(nameof(account));

        await _host.EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
        var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var storedAccount = FindStoredAccount(cachedAccounts, account);
        if (!_host.CanAuthenticateInteractively)
            throw new AuthenticationAttentionException(account);

        await _host.AcquireTokenInteractiveAsync(SubstrateTaskScopes, storedAccount,
            GetAuthenticationAddress(account), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTokenInformationAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        await _host.EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);

        if (account == null)
            return;

        ClearCachedAccessTokens(account);

        var authenticationAddress = string.IsNullOrWhiteSpace(account.AuthenticationAddress)
            ? account.Address
            : account.AuthenticationAddress;

        await _host.RemoveLocalAccountAsync(authenticationAddress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ResolveMailboxAddressAsync(string accessToken, string fallbackAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await GraphProfileHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return fallbackAddress;

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = document.RootElement;
            var mail = GetStringProperty(root, "mail");

            if (!string.IsNullOrWhiteSpace(mail))
                return mail;

            var userPrincipalName = GetStringProperty(root, "userPrincipalName");
            return string.IsNullOrWhiteSpace(userPrincipalName) ? fallbackAddress : userPrincipalName;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var scopes = GetScope(account, requiredFeatures);
        var cacheKey = GetAccessTokenCacheKey(account, scopes);
        var requestedOn = DateTimeOffset.UtcNow;

        if (!forceRefresh && TryGetReusableAccessToken(cacheKey, DateTimeOffset.MinValue, out var reusableToken))
            return reusableToken;

        var gate = AccessTokenGates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have acquired a token while this one waited. A forced refresh
            // accepts only a token issued after this request started.
            if (TryGetReusableAccessToken(cacheKey, forceRefresh ? requestedOn : DateTimeOffset.MinValue, out reusableToken))
                return reusableToken;

            var acquisition = await TryGetCachedTokenInformationAsync(account, scopes, forceRefresh, cancellationToken).ConfigureAwait(false);

            if (acquisition == null)
            {
                AccessTokens.TryRemove(cacheKey, out _);
                return null;
            }

            AccessTokens[cacheKey] = new CachedAccessToken(acquisition.TokenInformation, acquisition.ExpiresOn, DateTimeOffset.UtcNow);
            return acquisition.TokenInformation;
        }
        catch (MsalUiRequiredException ex)
        {
            AccessTokens.TryRemove(cacheKey, out _);
            LogSilentTokenFailure(ex, forceRefresh);
            return null;
        }
        catch (MsalClientException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AccessTokens.TryRemove(cacheKey, out _);
            LogSilentTokenFailure(ex, forceRefresh);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetReusableAccessToken(string cacheKey, DateTimeOffset issuedAfter, out TokenInformationEx tokenInformation)
    {
        tokenInformation = null;

        if (!AccessTokens.TryGetValue(cacheKey, out var cachedToken) ||
            cachedToken.AcquiredOn < issuedAfter ||
            cachedToken.ExpiresOn - AccessTokenReuseMargin <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        tokenInformation = cachedToken.TokenInformation;
        return true;
    }

    private static string GetAccessTokenCacheKey(MailAccount account, IEnumerable<string> scopes)
    {
        var normalizedScopes = scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(scope => scope, StringComparer.Ordinal);

        return $"{GetAccessTokenCacheAccountPrefix(account)}{string.Join(' ', normalizedScopes)}";
    }

    private static string GetAccessTokenCacheAccountPrefix(MailAccount account)
        => $"{account?.Id}|{GetAuthenticationAddress(account)?.Trim().ToLowerInvariant()}|";

    private static void ClearCachedAccessTokens(MailAccount account)
    {
        var prefix = GetAccessTokenCacheAccountPrefix(account);

        foreach (var key in AccessTokens.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            AccessTokens.TryRemove(key, out _);
        }
    }

    private static void LogSilentTokenFailure(MsalException exception, bool forceRefresh)
    {
        // Error codes and MSAL messages carry no tokens while MSAL PII logging stays disabled.
        Logger.Warning(
            "Silent Outlook token acquisition failed. ForceRefresh: {ForceRefresh}, Exception: {ExceptionType}, ErrorCode: {ErrorCode}, Classification: {Classification}, StatusCode: {StatusCode}, CorrelationId: {CorrelationId}, Message: {Message}",
            forceRefresh,
            exception.GetType().Name,
            exception.ErrorCode,
            (exception as MsalUiRequiredException)?.Classification,
            (exception as MsalServiceException)?.StatusCode,
            exception.CorrelationId,
            exception.Message);
    }

    private async Task<TokenAcquisition> TryGetCachedTokenInformationAsync(
        MailAccount account,
        string[] scopes,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachedAccounts = (await _publicClientApplication.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var storedAccount = FindStoredAccount(cachedAccounts, account);

        if (storedAccount != null)
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, storedAccount)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return new TokenAcquisition(
                new TokenInformationEx(authResult.AccessToken, account?.Address, authResult.Account.Username),
                authResult.ExpiresOn);
        }

        foreach (var cachedAccount in cachedAccounts)
        {
            var tokenInfo = await TryGetMatchingTokenInformationAsync(account, scopes, cachedAccount, forceRefresh, cancellationToken)
                .ConfigureAwait(false);

            if (tokenInfo != null)
                return tokenInfo;
        }

        return await TryGetMatchingTokenInformationAsync(
            account,
            scopes,
            PublicClientApplication.OperatingSystemAccount,
            forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenAcquisition> TryGetMatchingTokenInformationAsync(
        MailAccount account,
        IEnumerable<string> scopes,
        IAccount cachedAccount,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            var authResult = await _publicClientApplication
                .AcquireTokenSilent(scopes, cachedAccount)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            var tokenInformation = await GetValidatedTokenInformationAsync(account, authResult, cancellationToken).ConfigureAwait(false);

            return tokenInformation == null ? null : new TokenAcquisition(tokenInformation, authResult.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            LogSilentTokenFailure(ex, forceRefresh);
            return null;
        }
        catch (MsalClientException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LogSilentTokenFailure(ex, forceRefresh);
            return null;
        }
    }

    private async Task<TokenInformationEx> GetValidatedTokenInformationAsync(MailAccount account, AuthenticationResult authResult, CancellationToken cancellationToken)
    {
        if (account == null)
            return new TokenInformationEx(authResult.AccessToken, authResult.Account.Username, authResult.Account.Username);

        var authenticationAddress = GetAuthenticationAddress(account);

        if (AddressesMatch(authResult.Account.Username, authenticationAddress) ||
            AddressesMatch(authResult.Account.Username, account.Address))
        {
            return new TokenInformationEx(authResult.AccessToken, account.Address, authResult.Account.Username);
        }

        var mailboxAddress = await ResolveMailboxAddressAsync(authResult.AccessToken, authResult.Account.Username, cancellationToken)
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
