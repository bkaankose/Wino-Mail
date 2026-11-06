using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Identity.Client;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// MSAL public client for Android. The broker is enabled: when Microsoft Authenticator or Intune Company Portal is
/// installed, sign-in and silent token requests go through it, so an account already added there signs in with a tap
/// (work, school and personal accounts). Without a broker, interactive sign-in runs in the system browser (Custom Tabs
/// through MSAL's <c>AuthenticationActivity</c>). Both start for a result from the head's current activity; the head
/// forwards <c>OnActivityResult</c> through <see cref="AndroidActivityResults"/>, and its <c>MsalRedirectActivity</c> (a
/// <see cref="BrowserTabActivity"/> with the <c>msauth://&lt;package&gt;</c> intent filter) receives the browser redirect.
/// The broker validates the same <c>msauth://&lt;package&gt;/&lt;signature hash&gt;</c> redirect URI, and the manifest
/// declares the broker packages for package visibility. MSAL keeps its local token cache in its own SharedPreferences,
/// so no cache helper is attached; accounts signed in through the broker keep their tokens in the broker.
/// </summary>
public sealed class AndroidOutlookAuthenticationHost : IOutlookAuthenticationHost
{
    private readonly AndroidActivityResults _activityResults;

    public AndroidOutlookAuthenticationHost(IAuthenticatorConfig authenticatorConfig, Context context, AndroidActivityResults activityResults)
    {
        ArgumentNullException.ThrowIfNull(authenticatorConfig);
        ArgumentNullException.ThrowIfNull(context);
        _activityResults = activityResults ?? throw new ArgumentNullException(nameof(activityResults));
        RedirectUri = GetRedirectUri(context);
        EnableBrokerSerialization();
        Client = PublicClientApplicationBuilder.Create(authenticatorConfig.OutlookAuthenticatorClientId)
            .WithRedirectUri(RedirectUri)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithBroker(true)
            .WithWinoLogging()
            .Build();
    }

    public string RedirectUri { get; }

    /// <summary>
    /// MSAL 4.90.1 (and 4.91) serializes its Android broker requests (<c>BrokerRequest</c> and friends) with
    /// <c>JsonSerializer.Serialize(value, type, MsalJsonSerializerContext.Custom)</c>, but that source-generated context
    /// has no metadata for them and locks its options on creation, so every broker call failed with NoMetadataForType
    /// (Debug and Release alike). The context answers lookups through its options, so it gets a mutable copy of them
    /// whose resolver is the context itself followed by a reflection resolver: MSAL's own types keep their generated
    /// metadata and settings, and the broker types it lacks fall back to reflection. The build keeps MSAL's members for
    /// that fallback (TrimmerRootAssembly in build/Wino.Android.targets).
    /// </summary>
    private static void EnableBrokerSerialization()
    {
        var contextType = typeof(PublicClientApplication).Assembly.GetType("Microsoft.Identity.Client.Platforms.net.MsalJsonSerializerContext");
        var optionsField = typeof(JsonSerializerContext).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
        if (contextType?.GetProperty("Custom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is not JsonSerializerContext msalContext
            || optionsField is null)
        {
            Serilog.Log.Warning("MSAL serializer context not found; Android broker requests may fail.");
            return;
        }
        if (msalContext.Options.TypeInfoResolverChain.Any(static resolver => resolver is DefaultJsonTypeInfoResolver)) return;
        var options = new JsonSerializerOptions(msalContext.Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(msalContext, new DefaultJsonTypeInfoResolver())
        };
        optionsField.SetValue(msalContext, options);
    }

    public IPublicClientApplication Client { get; }

    /// <summary>True while the app has a started activity to open the browser from.</summary>
    public bool CanAuthenticateInteractively
        => _activityResults.GetCurrentActivityAsync().GetAwaiter().GetResult() is not null;

    /// <summary>MSAL persists its own cache on Android; there is nothing to attach.</summary>
    public Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes,
        IAccount? account, string? loginHint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = await _activityResults.GetCurrentActivityAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Interactive Android authentication requires a visible activity.");
        var request = Client.AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false)
            .WithParentActivityOrWindow(parent);
        if (account is not null) request = request.WithAccount(account);
        else if (!string.IsNullOrWhiteSpace(loginHint)) request = request.WithLoginHint(loginHint);
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
    {
        var accounts = await Client.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.Where(value => string.Equals(value.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase)))
            await Client.RemoveAsync(account).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Android redirect URI: <c>msauth://&lt;package&gt;/&lt;signature hash&gt;</c>, where the signature hash is
    /// Base64(SHA-1(signing certificate)), not URL-encoded. MSAL's broker check compares it with the raw form and MSAL
    /// URL-encodes it itself for the broker and Entra (the portal shows the encoded form). It differs per signing key
    /// (debug, release, Play App Signing), and each one must be registered on the app's Android platform entry.
    /// </summary>
    public static string GetRedirectUri(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var packageName = context.PackageName ?? throw new InvalidOperationException("The package name is unavailable.");
        return $"msauth://{packageName}/{GetSignatureHash(context, packageName)}";
    }

    private static string GetSignatureHash(Context context, string packageName)
    {
        var manager = context.PackageManager ?? throw new InvalidOperationException("The package manager is unavailable.");
        Signature[]? signatures;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var info = manager.GetPackageInfo(packageName, PackageManager.PackageInfoFlags.Of((long)PackageInfoFlags.SigningCertificates));
            signatures = GetSigningInfoSignatures(info?.SigningInfo);
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var info = manager.GetPackageInfo(packageName, PackageInfoFlags.SigningCertificates);
            signatures = GetSigningInfoSignatures(info?.SigningInfo);
        }
        else
        {
            var info = manager.GetPackageInfo(packageName, PackageInfoFlags.Signatures);
            signatures = info?.Signatures?.ToArray();
        }

        var certificate = signatures?.FirstOrDefault()?.ToByteArray()
            ?? throw new InvalidOperationException("The app's signing certificate is unavailable.");
        return Convert.ToBase64String(SHA1.HashData(certificate));
    }

    /// <summary>The certificates that signed the APK contents (the current signer, also after key rotation).</summary>
    private static Signature[]? GetSigningInfoSignatures(SigningInfo? signingInfo)
        => signingInfo is not null && OperatingSystem.IsAndroidVersionAtLeast(28) ? signingInfo.GetApkContentsSigners()?.ToArray() : null;
}
