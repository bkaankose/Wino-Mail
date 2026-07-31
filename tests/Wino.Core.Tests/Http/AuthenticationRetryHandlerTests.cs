using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Http;
using Xunit;

namespace Wino.Core.Tests.Http;

public class AuthenticationRetryHandlerTests
{
    [Fact]
    public async Task GmailHandler_UnauthorizedResponse_RefreshesSilentlyAndRetriesOnce()
    {
        var account = new MailAccount();
        var authenticator = new Mock<IGmailAuthenticator>();
        var transport = new UnauthorizedOnceHandler();

        authenticator
            .Setup(x => x.GetTokenInformationAsync(account))
            .ReturnsAsync(new TokenInformationEx("stale-token", account.Address));
        authenticator
            .Setup(x => x.RefreshTokenInformationAsync(account))
            .ReturnsAsync(new TokenInformationEx("fresh-token", account.Address));

        using var handler = new GmailClientMessageHandler(authenticator.Object, account)
        {
            InnerHandler = transport
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("https://gmail.googleapis.com/gmail/v1/users/me/profile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.AuthorizationParameters.Should().Equal("stale-token", "fresh-token");
        authenticator.Verify(x => x.RefreshTokenInformationAsync(account), Times.Once);
    }

    [Fact]
    public async Task GraphHandler_UnauthorizedResponse_RefreshesSilentlyAndRetriesOnce()
    {
        var account = new MailAccount();
        var authenticator = new Mock<IAuthenticator>();
        var transport = new UnauthorizedOnceHandler();

        authenticator
            .Setup(x => x.RefreshTokenInformationAsync(account))
            .ReturnsAsync(new TokenInformationEx("fresh-token", account.Address));

        using var handler = new GraphAuthenticationRetryHandler(account, authenticator.Object)
        {
            InnerHandler = transport
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stale-token");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.AuthorizationParameters.Should().Equal("stale-token", "fresh-token");
        authenticator.Verify(x => x.RefreshTokenInformationAsync(account), Times.Once);
    }

    private sealed class UnauthorizedOnceHandler : HttpMessageHandler
    {
        public List<string> AuthorizationParameters { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter ?? string.Empty);

            return Task.FromResult(new HttpResponseMessage(
                AuthorizationParameters.Count == 1
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK));
        }
    }
}
