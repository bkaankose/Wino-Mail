using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Common;
using Android.Gms.Common.Apis;
using Android.Gms.Extensions;
using Serilog;
using Wino.Authentication;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using GoogleAccount = Android.Accounts.Account;
using GoogleScope = Android.Gms.Common.Apis.Scope;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// Gmail on Android. An account with a stored Google refresh token (signed in through the loopback flow) keeps using
/// the shared <see cref="GmailAuthenticator"/>. Otherwise, when Google Play services is available, access tokens come
/// from Play services' <c>AuthorizationClient</c>: no client id in code (Google matches the package name and signing
/// certificate to the project's Android OAuth client), no refresh token on the device, and a silent <c>authorize</c>
/// returns a fresh token while the grant stands. Without Play services the shared loopback flow runs in a Custom Tab.
/// A silent request that needs the user (revoked grant, new scopes) raises <see cref="AuthenticationAttentionException"/>,
/// the same signal the shared authenticator uses for an invalid refresh token.
/// </summary>
public sealed class AndroidGmailAuthenticator(
    Context context,
    IAuthenticatorConfig authenticatorConfig,
    IGoogleTokenStore tokenStore,
    GmailAuthenticator loopbackAuthenticator,
    AndroidActivityResults activityResults) : IGmailAuthenticator
{
    /// <summary>Google account type for <see cref="AuthorizationRequest.Builder.SetAccount"/>.</summary>
    private const string GoogleAccountType = "com.google";

    /// <summary>OpenID userinfo endpoint (accounts.google.com discovery document); needs the userinfo.email scope.</summary>
    private static readonly Uri UserInfoUri = new("https://openidconnect.googleapis.com/v1/userinfo");

    /// <summary>
    /// How long a token from Play services is reused before asking it again. Play services keeps its own cache, so a
    /// repeat request is local; the short window only saves the round trip for every Google API call. A token rejected
    /// earlier is replaced through <see cref="RefreshTokenInformationAsync"/>.
    /// </summary>
    private static readonly TimeSpan TokenReuseWindow = TimeSpan.FromMinutes(10);

    private static readonly HttpClient HttpClient = new();
    private static readonly ILogger Logger = Log.ForContext<AndroidGmailAuthenticator>();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    public MailProviderType ProviderType => MailProviderType.Gmail;

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount? account,
        IReadOnlyCollection<ProviderFeature>? requestedFeatures = null, CancellationToken cancellationToken = default)
    {
        if (!await UsesPlayServicesAsync(account, cancellationToken).ConfigureAwait(false))
            return await loopbackAuthenticator.GenerateTokenInformationAsync(account, requestedFeatures, cancellationToken).ConfigureAwait(false);

        return await WithLockAsync(account, () => AuthorizeAsync(account, requestedFeatures, interactive: true, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount? account,
        IReadOnlyCollection<ProviderFeature>? requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        if (!await UsesPlayServicesAsync(account, cancellationToken).ConfigureAwait(false))
            return await loopbackAuthenticator.GetTokenInformationAsync(account, requiredFeatures, cancellationToken).ConfigureAwait(false);

        return await WithLockAsync(account, async () =>
        {
            if (_tokens.TryGetValue(GetCredentialKey(account), out var cached) && cached.IsFresh)
                return CreateTokenInformation(account, cached.AccessToken, null);
            return await AuthorizeAsync(account, requiredFeatures, interactive: false, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TokenInformationEx> RefreshTokenInformationAsync(MailAccount? account,
        IReadOnlyCollection<ProviderFeature>? requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        if (!await UsesPlayServicesAsync(account, cancellationToken).ConfigureAwait(false))
            return await loopbackAuthenticator.RefreshTokenInformationAsync(account, requiredFeatures, cancellationToken).ConfigureAwait(false);

        return await WithLockAsync(account, async () =>
        {
            // The resource server rejected the token: drop it from Play services' cache so the next request goes to Google.
            await ClearCachedTokenAsync(account).ConfigureAwait(false);
            return await AuthorizeAsync(account, requiredFeatures, interactive: false, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Play services' <c>AuthorizationClient</c> returns access tokens only, so those accounts get no ID token.</summary>
    public async Task<string?> GetIdentityTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (!await UsesPlayServicesAsync(account, cancellationToken).ConfigureAwait(false))
            return await loopbackAuthenticator.GetIdentityTokenAsync(account, cancellationToken).ConfigureAwait(false);

        return null;
    }

    public async Task DeleteTokenInformationAsync(MailAccount? account, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(account, async () =>
        {
            await ClearCachedTokenAsync(account).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        await loopbackAuthenticator.DeleteTokenInformationAsync(account, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The selection rule: a stored refresh token keeps the loopback flow that created it; otherwise Play services when
    /// it is available; otherwise the loopback flow.
    /// </summary>
    private async Task<bool> UsesPlayServicesAsync(MailAccount? account, CancellationToken cancellationToken)
    {
        var stored = await tokenStore.ReadAsync(GetCredentialKey(account), cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            CryptographicOperations.ZeroMemory(stored);
            return false;
        }

        return GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(context) == ConnectionResult.Success;
    }

    private async Task<TokenInformationEx> AuthorizeAsync(MailAccount? account,
        IReadOnlyCollection<ProviderFeature>? features, bool interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scopes = authenticatorConfig.GetGmailScopes(ProviderAuthorizationRequest.ForAccount(account, features));
        var googleAddress = GetGoogleAccountAddress(account);

        var builder = AuthorizationRequest.InvokeBuilder()
            .SetRequestedScopes(scopes.Select(scope => new GoogleScope(scope)).ToList());
        if (googleAddress is not null)
            builder = builder.SetAccount(new GoogleAccount(googleAddress, GoogleAccountType));
        else if (interactive)
            builder = builder.SetPrompt(AuthorizationRequest.Prompt.SelectAccount); // A new account: let the user pick it.
        var request = builder.Build();

        var activity = interactive ? await activityResults.GetCurrentActivityAsync().ConfigureAwait(false) : null;
        if (interactive && activity is null)
            throw new InvalidOperationException("Interactive Google authorization requires a visible activity.");
        var client = activity is null ? Identity.GetAuthorizationClient(context) : Identity.GetAuthorizationClient(activity);

        AuthorizationResult result;
        try
        {
            result = await client.Authorize(request).AsAsync<AuthorizationResult>().ConfigureAwait(false);
        }
        catch (ApiException error)
        {
            LogApiException(account, error);
            throw;
        }

        if (result.HasResolution)
        {
            if (!interactive || result.PendingIntent?.IntentSender is not { } intentSender)
            {
                Logger.Information("Google Play services needs the user to authorize account {AccountId} again.", account?.Id);
                throw new AuthenticationAttentionException(account);
            }

            var activityResult = await activityResults.StartIntentSenderAsync(intentSender, cancellationToken).ConfigureAwait(false);
            if (activityResult.ResultCode != Result.Ok || activityResult.Data is null)
                throw new AccountSetupCanceledException();

            try
            {
                result = client.GetAuthorizationResultFromIntent(activityResult.Data);
            }
            catch (ApiException error) when (error.StatusCode == CommonStatusCodes.Canceled)
            {
                throw new AccountSetupCanceledException();
            }
            catch (ApiException error)
            {
                LogApiException(account, error);
                throw;
            }
        }

        var accessToken = result.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthMissingPermissions);

        // The consent screen lets users untick individual permissions; refuse a partial grant like the shared flow.
        var missingScopes = GmailAuthenticator.GetMissingScopes(scopes, string.Join(' ', result.GrantedScopes ?? []));
        if (missingScopes.Count > 0)
        {
            Logger.Warning("Google sign-in for account {AccountId} did not grant {MissingScopes}.", account?.Id, missingScopes);
            throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthMissingPermissions);
        }

        _tokens[GetCredentialKey(account)] = new CachedToken(accessToken, DateTimeOffset.UtcNow);

        // A new account learns which Google account the user picked; later silent requests ask for that account.
        if (interactive && googleAddress is null)
            googleAddress = await ReadAddressAsync(accessToken, cancellationToken).ConfigureAwait(false);

        return CreateTokenInformation(account, accessToken, googleAddress);
    }

    private async Task ClearCachedTokenAsync(MailAccount? account)
    {
        if (!_tokens.TryRemove(GetCredentialKey(account), out var cached)) return;
        try
        {
            var clearRequest = ClearTokenRequest.InvokeBuilder().SetToken(cached.AccessToken).Build();
            await Identity.GetAuthorizationClient(context).ClearToken(clearRequest).AsAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The next authorize may return the same token; the request then fails again and is retried later.
            Logger.Warning(error, "Google Play services could not clear the cached token of account {AccountId}.", account?.Id);
        }
    }

    /// <summary>The signed-in Google address from the OpenID userinfo endpoint ("email" claim).</summary>
    private static async Task<string> ReadAddressAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address)
            return address;
        throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthMissingPermissions);
    }

    private static TokenInformationEx CreateTokenInformation(MailAccount? account, string accessToken, string? googleAddress)
    {
        var authenticationAddress = googleAddress ?? GetGoogleAccountAddress(account);
        var accountAddress = string.IsNullOrWhiteSpace(account?.Address) ? authenticationAddress : account.Address;
        return new TokenInformationEx(accessToken, accountAddress ?? string.Empty, authenticationAddress);
    }

    /// <summary>The Google account the grant belongs to: the authentication address, else the mailbox address.</summary>
    private static string? GetGoogleAccountAddress(MailAccount? account)
    {
        if (!string.IsNullOrWhiteSpace(account?.AuthenticationAddress)) return account.AuthenticationAddress;
        if (!string.IsNullOrWhiteSpace(account?.Address)) return account.Address;
        return null;
    }

    private static void LogApiException(MailAccount? account, ApiException error)
    {
        // DEVELOPER_ERROR: no Android OAuth client matches this package name and signing certificate SHA-1.
        if (error.StatusCode == CommonStatusCodes.DeveloperError)
            Logger.Error("Google Play services rejected the app registration (DEVELOPER_ERROR) for account {AccountId}. Register an Android OAuth client for this package and signing certificate.", account?.Id);
        else
            Logger.Warning("Google Play services authorization failed for account {AccountId} with status {StatusCode}.", account?.Id, error.StatusCode);
    }

    private static string GetCredentialKey(MailAccount? account) => account?.Id.ToString("N") ?? "default";

    private static async Task<T> WithLockAsync<T>(MailAccount? account, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(GetCredentialKey(account), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset IssuedAtUtc)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - IssuedAtUtc < TokenReuseWindow;
    }
}
