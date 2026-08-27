using System.Net;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class DavTransportTests
{
    [Fact]
    public async Task SendAsync_BasicAuthenticationOverHttp_IsRejectedBeforeSending()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new DavTransport(new HttpClient(handler));

        var action = () => transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://dav.example.test/contacts"),
            BasicProfile());

        await action.Should().ThrowAsync<InvalidOperationException>();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_CrossOriginRedirect_DoesNotSendCredentialsToRedirectTarget()
    {
        var handler = new SequenceHandler(request => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.test/contacts") }
        });
        var transport = new DavTransport(new HttpClient(handler));

        var action = () => transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://dav.example.test/contacts"),
            BasicProfile());

        var exception = await action.Should().ThrowAsync<DavRequestException>();

        exception.Which.Message.Should().Be(Translator.DavError_Network);
        handler.RequestCount.Should().Be(1);
        handler.Origins.Should().Equal("dav.example.test");
    }

    [Fact]
    public async Task SendAsync_SameOriginRedirect_RecreatesAuthenticatedRequest()
    {
        var handler = new SequenceHandler(request => request.RequestUri!.AbsolutePath == "/contacts"
            ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("/address-books", UriKind.Relative) }
            }
            : new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new DavTransport(new HttpClient(handler));

        using var response = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://dav.example.test/contacts"),
            BasicProfile());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestCount.Should().Be(2);
        handler.Origins.Should().Equal("dav.example.test", "dav.example.test");
        handler.AuthorizationSchemes.Should().Equal("Basic", "Basic");
    }

    private static DavAuthenticationProfile BasicProfile() => new()
    {
        Kind = DavAuthenticationKind.Basic,
        Username = "dav-user",
        Password = "app-password"
    };

    private sealed class SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> Origins { get; } = [];
        public List<string?> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Origins.Add(request.RequestUri!.Host);
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            return Task.FromResult(responseFactory(request));
        }
    }
}
