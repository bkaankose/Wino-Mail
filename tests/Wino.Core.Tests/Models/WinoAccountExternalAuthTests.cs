using System;
using FluentAssertions;
using Wino.Core.Domain.Models.Accounts;
using Xunit;

namespace Wino.Core.Tests.Models;

public class WinoAccountExternalAuthTests
{
    [Fact]
    public void CallbackUri_MatchesTheSchemeAndHost()
    {
        WinoAccountExternalAuth.CallbackUri.Should().Be(new Uri("wino-mail://auth-complete"));
        WinoAccountExternalAuth.IsCallback(WinoAccountExternalAuth.CallbackUri).Should().BeTrue();
    }

    [Fact]
    public void AuthorizePath_IsTheProviderRoute()
        => WinoAccountExternalAuth.AuthorizePath(WinoAccountExternalAuth.AppleProvider).Should().Be("api/v1/auth/apple/authorize");

    [Fact]
    public void ParseCallback_ReadsTheCompletionCode()
    {
        var result = WinoAccountExternalAuth.ParseCallback(new Uri("wino-mail://auth-complete?code=abc123"));

        result.Code.Should().Be("abc123");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void ParseCallback_ReadsTheErrorCode()
    {
        var result = WinoAccountExternalAuth.ParseCallback(new Uri("wino-mail://auth-complete?errorCode=EXTERNAL_LOGIN_INVALID"));

        result.Code.Should().BeNull();
        result.ErrorCode.Should().Be("EXTERNAL_LOGIN_INVALID");
    }

    [Fact]
    public void ParseCallback_UnescapesTheCode()
        => WinoAccountExternalAuth.ParseCallback(new Uri("wino-mail://auth-complete?code=a%2Bb%3D%3D&errorCode=")).Code.Should().Be("a+b==");

    [Theory]
    [InlineData("wino://auth-complete?code=abc")]
    [InlineData("wino-mail://billing?code=abc")]
    [InlineData("https://api.winomail.app/api/v1/auth/apple/callback?code=abc")]
    public void ParseCallback_IgnoresOtherAddresses(string address)
    {
        var uri = new Uri(address);

        WinoAccountExternalAuth.IsCallback(uri).Should().BeFalse();
        WinoAccountExternalAuth.ParseCallback(uri).Should().Be(new WinoAccountExternalAuthCallback(null, null));
    }

    [Fact]
    public void ParseCallback_WithoutQuery_HasNeitherValue()
        => WinoAccountExternalAuth.ParseCallback(new Uri("wino-mail://auth-complete")).Should().Be(new WinoAccountExternalAuthCallback(null, null));

    [Fact]
    public void ParseCallback_Null_HasNeitherValue()
        => WinoAccountExternalAuth.ParseCallback(null).Should().Be(new WinoAccountExternalAuthCallback(null, null));
}
