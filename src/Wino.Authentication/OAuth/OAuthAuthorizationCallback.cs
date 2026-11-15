#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;

namespace Wino.Authentication.OAuth;

internal static class OAuthPkce
{
    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    public static string CreateChallenge(string codeVerifier)
        => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Validates an authorization response (custom scheme or loopback) and returns its code.</summary>
internal static class OAuthAuthorizationCallback
{
    /// <param name="query">The raw query string, with or without the leading '?'.</param>
    /// <param name="hasFragment">A response in the fragment means an implicit or hybrid flow Wino never requests.</param>
    public static string ReadCode(string? query, bool hasFragment, string expectedState, string issuer)
    {
        if (hasFragment)
            throw Failed("The sign-in response used an unexpected format.");

        var parameters = ParseQuery(query);
        parameters.TryGetValue("code", out var code);
        parameters.TryGetValue("error", out var error);
        parameters.TryGetValue("state", out var state);

        if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(error))
            throw Failed("The sign-in response carried both a code and an error.");

        if (string.IsNullOrEmpty(state) || !StateEquals(state, expectedState))
            throw Failed("The sign-in response could not be verified.");

        // RFC 9207: when the server names itself, it must be the issuer this sign-in started with.
        if (parameters.TryGetValue("iss", out var responseIssuer) && !OAuthProtocolClient.IssuerEquals(responseIssuer, issuer))
            throw Failed("The sign-in response came from a different issuer.");

        if (!string.IsNullOrEmpty(error))
        {
            if (string.Equals(error, "access_denied", StringComparison.Ordinal))
                throw new OAuthSignInException(OAuthSignInFailureKind.Denied, "Access was declined.");

            throw Failed("The sign-in did not complete.");
        }

        if (string.IsNullOrEmpty(code))
            throw Failed("The sign-in response carried no code.");

        return code;
    }

    private static bool StateEquals(string actual, string expected)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return values;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Unescape(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Unescape(pair[(separator + 1)..]);

            // A repeated parameter is ambiguous; refuse the response rather than pick one.
            if (!values.TryAdd(key, value))
                throw Failed("The sign-in response repeated a parameter.");
        }

        return values;
    }

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static OAuthSignInException Failed(string message) => new(OAuthSignInFailureKind.Failed, message);
}
