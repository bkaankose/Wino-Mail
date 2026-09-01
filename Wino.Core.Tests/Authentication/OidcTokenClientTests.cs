using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wino.Core.Authentication;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class OidcTokenClientTests
{
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void CreatePkcePair_ChallengeIsS256OfVerifier()
    {
        var client = new OidcTokenClient();

        var pair = client.CreatePkcePair();

        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier)));
        pair.Challenge.Should().Be(expectedChallenge);
    }

    [Fact]
    public void CreatePkcePair_IsUrlSafeAndUnpadded()
    {
        var client = new OidcTokenClient();

        var pair = client.CreatePkcePair();

        foreach (var value in new[] { pair.Verifier, pair.Challenge })
        {
            value.Should().NotBeNullOrEmpty();
            value.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
        }
    }

    [Fact]
    public void CreatePkcePair_ProducesUniquePairs()
    {
        var client = new OidcTokenClient();

        client.CreatePkcePair().Verifier.Should().NotBe(client.CreatePkcePair().Verifier);
    }

    [Fact]
    public void BuildAuthorizationUrl_ContainsPkceAndConfiguration()
    {
        var client = new OidcTokenClient();
        var discovery = new OidcDiscoveryDocument { AuthorizationEndpoint = "https://wsfed.example.com/adfs/oauth2/authorize" };
        var configuration = new OidcConfiguration
        {
            ClientId = "00000002-0000-0ff1-ce00-000000000000",
            RedirectUri = "https://mail.example.com/owa/",
            Resource = "https://mail.example.com/",
            Scope = "openid offline_access",
        };

        var url = client.BuildAuthorizationUrl(discovery, configuration, "CHALLENGE123", "STATE456");

        url.Should().StartWith("https://wsfed.example.com/adfs/oauth2/authorize?");
        url.Should().Contain("response_type=code");
        url.Should().Contain("client_id=00000002-0000-0ff1-ce00-000000000000");
        url.Should().Contain("code_challenge=CHALLENGE123");
        url.Should().Contain("code_challenge_method=S256");
        url.Should().Contain("state=STATE456");
        url.Should().Contain($"redirect_uri={Uri.EscapeDataString(configuration.RedirectUri)}");
        url.Should().Contain($"resource={Uri.EscapeDataString(configuration.Resource)}");
        url.Should().Contain($"scope={Uri.EscapeDataString(configuration.Scope)}");
    }

    [Fact]
    public void OidcTokenSet_IsAccessTokenValid_RespectsExpiryAndSkew()
    {
        var valid = new OidcTokenSet { AccessToken = "t", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) };
        var expiring = new OidcTokenSet { AccessToken = "t", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30) };
        var empty = new OidcTokenSet { AccessToken = "", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10) };

        valid.IsAccessTokenValid(TimeSpan.FromMinutes(2)).Should().BeTrue();
        expiring.IsAccessTokenValid(TimeSpan.FromMinutes(2)).Should().BeFalse();
        empty.IsAccessTokenValid(TimeSpan.FromMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void ParseTokenResponse_RejectsNonBearerToken()
    {
        var body = """
        {
            "access_token": "token",
            "token_type": "pop",
            "expires_in": 3600
        }
        """;

        var act = () => OidcTokenClient.ParseTokenResponse(body, DateTimeOffset.UtcNow);

        act.Should().Throw<OidcTokenException>()
            .WithMessage("*unsupported token_type*");
    }

    [Fact]
    public void ParseTokenResponse_MissingExpiresIn_IsImmediatelyExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var body = """
        {
            "access_token": "token",
            "token_type": "Bearer",
            "refresh_token": "refresh"
        }
        """;

        var result = OidcTokenClient.ParseTokenResponse(body, now);

        result.AccessToken.Should().Be("token");
        result.RefreshToken.Should().Be("refresh");
        result.ExpiresAtUtc.Should().Be(now);
    }
}
