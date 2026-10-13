using Wino.Authentication;
using Xunit;

namespace Wino.Core.Tests.Authentication;

public sealed class GoogleCustomSchemeRedirectTests
{
    private const string ClientId = "973025879644-AbC123.apps.googleusercontent.com";

    [Fact]
    public void GetScheme_ReversesTheClientId()
        => Assert.Equal("com.googleusercontent.apps.973025879644-abc123", GoogleCustomSchemeRedirect.GetScheme(ClientId));

    [Fact]
    public void GetRedirectUri_UsesTheOAuthRedirectPath()
        => Assert.Equal("com.googleusercontent.apps.973025879644-abc123:/oauthredirect", GoogleCustomSchemeRedirect.GetRedirectUri(ClientId).OriginalString);

    [Theory]
    [InlineData("")]
    [InlineData("973025879644-abc")]
    [InlineData(".apps.googleusercontent.com")]
    [InlineData("bad id.apps.googleusercontent.com")]
    public void GetScheme_RejectsValuesThatAreNotClientIds(string value)
        => Assert.Throws<ArgumentException>(() => GoogleCustomSchemeRedirect.GetScheme(value));

    [Fact]
    public void ParseCallback_ReturnsTheCode_WhenStateMatches()
    {
        var redirect = GoogleCustomSchemeRedirect.GetRedirectUri(ClientId);
        var result = GoogleCustomSchemeRedirect.ParseCallback(new Uri(redirect.OriginalString + "?state=s1&code=4%2F0Ab-x&scope=email"), redirect, "s1");

        Assert.True(result.IsSuccess);
        Assert.Equal("4/0Ab-x", result.Code);
    }

    [Theory]
    [InlineData("?state=other&code=c", "invalid_state")]
    [InlineData("?code=c", "invalid_state")]
    [InlineData("?state=s1&error=access_denied", "access_denied")]
    [InlineData("?state=s1", "invalid_code")]
    public void ParseCallback_ReportsFailures(string query, string error)
    {
        var redirect = GoogleCustomSchemeRedirect.GetRedirectUri(ClientId);
        var result = GoogleCustomSchemeRedirect.ParseCallback(new Uri(redirect.OriginalString + query), redirect, "s1");

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void ParseCallback_RejectsAnotherScheme()
    {
        var redirect = GoogleCustomSchemeRedirect.GetRedirectUri(ClientId);
        var result = GoogleCustomSchemeRedirect.ParseCallback(new Uri("com.example.other:/oauthredirect?state=s1&code=c"), redirect, "s1");

        Assert.Equal("invalid_redirect", result.Error);
    }
}
