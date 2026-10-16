#nullable enable
using System;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>What the API's browser sign-in flow sent back to the app: a completion code or an error code.</summary>
public sealed record WinoAccountExternalAuthCallback(string? Code, string? ErrorCode);

/// <summary>
/// The Wino Account browser sign-in flow (Android, Mac DMG builds, Windows): the app opens
/// <c>api/v1/auth/{provider}/authorize</c>, the API finishes with the provider and redirects to <see cref="CallbackUri"/>
/// with <c>?code=</c> (a two-minute completion code for <c>api/v1/auth/external/complete</c>) or <c>?errorCode=</c>.
/// The callback address is fixed on the API side (<c>Secrets:ExternalAuth:AppCallbackUri</c>); this is the only place
/// the client spells it, so the Android intent filter and the Mac web session read it from here.
/// </summary>
public static class WinoAccountExternalAuth
{
    public const string AppleProvider = "apple";
    public const string GoogleProvider = "google";

    /// <summary>
    /// The Wino Accounts Google iOS client (Google Cloud project 973025879644), shared by the iOS and Mac apps through
    /// their common bundle id. The API lists it in <c>Secrets:Google:NativeClientIds</c>. Not the Windows app's Gmail
    /// mailbox client, which is never used for Wino Accounts.
    /// </summary>
    public const string GoogleAppleClientId = "973025879644-qlc58h7f2gepqgu3ocvlr7mqvd3fff1c.apps.googleusercontent.com";

    /// <summary>
    /// The Wino Accounts Google web client: Android's Credential Manager <c>serverClientId</c>, so the ID token's audience is
    /// the API's own client id. The Android OAuth clients themselves are matched by package and certificate, never named.
    /// </summary>
    public const string GoogleAndroidServerClientId = "973025879644-92h0mdbvgaiq0p6efdv16hpiop65er6a.apps.googleusercontent.com";

    public const string CallbackScheme = "wino-mail";
    public const string CallbackHost = "auth-complete";

    public static readonly Uri CallbackUri = new($"{CallbackScheme}://{CallbackHost}");

    /// <summary>How long the API accepts a completion code; the browser wait gives up after it.</summary>
    public static readonly TimeSpan CompletionCodeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The relative API path that starts the browser flow for <paramref name="provider"/>.</summary>
    public static string AuthorizePath(string provider) => $"api/v1/auth/{provider}/authorize";

    public static bool IsCallback(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && string.Equals(uri.Scheme, CallbackScheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(uri.Host, CallbackHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads <c>code</c> and <c>errorCode</c> from a callback; both null when the address is not the callback.</summary>
    public static WinoAccountExternalAuthCallback ParseCallback(Uri? uri)
    {
        if (!IsCallback(uri) || uri is null) return new WinoAccountExternalAuthCallback(null, null);

        string? code = null, errorCode = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase)) code ??= Clean(value);
            else if (string.Equals(key, "errorCode", StringComparison.OrdinalIgnoreCase)) errorCode ??= Clean(value);
        }
        return new WinoAccountExternalAuthCallback(code, errorCode);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
