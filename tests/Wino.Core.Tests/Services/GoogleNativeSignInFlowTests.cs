using System.Net;
using System.Text;
using FluentAssertions;
using Wino.Authentication;
using Wino.Core.Domain.Models.Accounts;
using Xunit;

namespace Wino.Core.Tests.Services;

public class GoogleNativeSignInFlowTests
{
    [Fact]
    public void CreateRequest_BuildsAnOpenIdRequestWithPkceForTheReversedClientScheme()
    {
        var request = GoogleNativeSignInFlow.CreateRequest(WinoAccountExternalAuth.GoogleAppleClientId);

        request.RedirectUri.Should().Be(new Uri("com.googleusercontent.apps.973025879644-qlc58h7f2gepqgu3ocvlr7mqvd3fff1c:/oauthredirect"));
        var query = request.AuthorizationUri.Query;
        request.AuthorizationUri.GetLeftPart(UriPartial.Path).Should().Be("https://accounts.google.com/o/oauth2/v2/auth");
        query.Should().Contain("client_id=" + Uri.EscapeDataString(WinoAccountExternalAuth.GoogleAppleClientId));
        query.Should().Contain("scope=openid%20email%20profile");
        query.Should().Contain("response_type=code");
        query.Should().Contain("state=" + request.State);
        query.Should().Contain("code_challenge_method=S256");
        query.Should().NotContain("access_type").And.NotContain("prompt=consent");
        GoogleNativeSignInFlow.CreateRequest(WinoAccountExternalAuth.GoogleAppleClientId).State.Should().NotBe(request.State);
    }

    [Fact]
    public async Task ExchangeForIdTokenAsync_PostsTheCodeAndReadsTheIdToken()
    {
        var request = GoogleNativeSignInFlow.CreateRequest(WinoAccountExternalAuth.GoogleAppleClientId);
        string? body = null;
        using var client = new HttpClient(new StubHandler(async message =>
        {
            body = await message.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"a\",\"id_token\":\"jwt-id\"}", Encoding.UTF8, "application/json") };
        }));

        var idToken = await GoogleNativeSignInFlow.ExchangeForIdTokenAsync(client, request, new Uri($"{request.RedirectUri.AbsoluteUri}?state={request.State}&code=the-code"), default);

        idToken.Should().Be("jwt-id");
        body.Should().Contain("code=the-code").And.Contain("grant_type=authorization_code").And.Contain("code_verifier=" + request.CodeVerifier).And.NotContain("client_secret");
    }

    [Fact]
    public async Task ExchangeForIdTokenAsync_RejectsAForeignState()
    {
        var request = GoogleNativeSignInFlow.CreateRequest(WinoAccountExternalAuth.GoogleAppleClientId);
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("must not be called")));

        var act = () => GoogleNativeSignInFlow.ExchangeForIdTokenAsync(client, request, new Uri($"{request.RedirectUri.AbsoluteUri}?state=other&code=the-code"), default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*invalid state*");
    }

    [Fact]
    public async Task ExchangeForIdTokenAsync_SurfacesOnlyTheOAuthErrorCode()
    {
        var request = GoogleNativeSignInFlow.CreateRequest(WinoAccountExternalAuth.GoogleAppleClientId);
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"secret details\"}", Encoding.UTF8, "application/json")
        })));

        var act = () => GoogleNativeSignInFlow.ExchangeForIdTokenAsync(client, request, new Uri($"{request.RedirectUri.AbsoluteUri}?state={request.State}&code=x"), default);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("invalid_grant").And.NotContain("secret details");
    }

    [Fact]
    public void ReadIdToken_IsNullWithoutATokenOrForBadJson()
    {
        GoogleNativeSignInFlow.ReadIdToken("{\"access_token\":\"a\"}").Should().BeNull();
        GoogleNativeSignInFlow.ReadIdToken("not json").Should().BeNull();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
