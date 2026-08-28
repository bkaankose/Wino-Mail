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
    public async Task GmailHandler_ParallelUnauthorizedResponses_ShareOneRefresh()
    {
        var account = new MailAccount { Id = Guid.NewGuid() };
        var authenticator = new Mock<IGmailAuthenticator>();
        var transport = new RejectStaleTokenHandler();
        var currentToken = "stale-token";

        authenticator
            .Setup(x => x.GetTokenInformationAsync(account))
            .ReturnsAsync(() => new TokenInformationEx(currentToken, account.Address));
        authenticator
            .Setup(x => x.RefreshTokenInformationAsync(account))
            .ReturnsAsync(() =>
            {
                currentToken = "fresh-token";
                return new TokenInformationEx(currentToken, account.Address);
            });

        using var handler = new GmailClientMessageHandler(authenticator.Object, account)
        {
            InnerHandler = transport
        };
        using var client = new HttpClient(handler);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => client.GetAsync($"https://gmail.googleapis.com/gmail/v1/users/me/messages/{index}")));

        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            authenticator.Verify(x => x.RefreshTokenInformationAsync(account), Times.Once);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
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

    private sealed class RejectStaleTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(
                request.Headers.Authorization?.Parameter == "stale-token"
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK));
    }
}
