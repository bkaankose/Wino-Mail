using System;
using System.Security.Cryptography;
using System.Text;

namespace Wino.Authentication;

/// <summary>State and PKCE (S256) helpers shared by every <see cref="IGoogleAuthorizationCodeReceiver"/>.</summary>
public static class GoogleAuthorizationPkce
{
    public static string CreateState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    public static Uri AppendChallenge(Uri authorizationUri, string codeVerifier)
    {
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var separator = string.IsNullOrEmpty(authorizationUri.Query) ? "?" : "&";
        return new Uri($"{authorizationUri.AbsoluteUri}{separator}code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256");
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
