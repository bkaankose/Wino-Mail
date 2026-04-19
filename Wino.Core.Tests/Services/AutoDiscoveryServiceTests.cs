using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class AutoDiscoveryServiceTests
{
    [Fact]
    public async Task GetAutoDiscoverySettings_UsesThunderbirdAutoconfig_WhenAvailable()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.StartsWith("https://autoconfig.example.com/mail/config-v1.1.xml", StringComparison.OrdinalIgnoreCase))
            {
                return CreateXmlResponse("""
                    <clientConfig version="1.1">
                      <emailProvider id="example.com">
                        <incomingServer type="imap">
                          <hostname>imap.example.com</hostname>
                          <port>993</port>
                          <socketType>SSL</socketType>
                          <username>%EMAILLOCALPART%</username>
                        </incomingServer>
                        <outgoingServer type="smtp">
                          <hostname>smtp.example.com</hostname>
                          <port>587</port>
                          <socketType>STARTTLS</socketType>
                          <username>%EMAILADDRESS%</username>
                        </outgoingServer>
                      </emailProvider>
                    </clientConfig>
                    """, request);
            }

            return CreateStatusResponse(HttpStatusCode.NotFound, request);
        });

        using var client = new HttpClient(handler);
        var sut = new AutoDiscoveryService(client);

        var settings = await sut.GetAutoDiscoverySettings(new AutoDiscoveryMinimalSettings
        {
            Email = "user@example.com",
            DisplayName = "User",
            Password = "secret"
        });

        settings.Should().NotBeNull();
        settings!.Domain.Should().Be("example.com");
        settings.GetImapSettings()!.Address.Should().Be("imap.example.com");
        settings.GetImapSettings()!.Username.Should().Be("user");
        settings.GetSmptpSettings()!.Address.Should().Be("smtp.example.com");
        settings.GetSmptpSettings()!.Username.Should().Be("user@example.com");
        handler.RequestedUris.Should().NotContain(uri => uri.Contains("emailsettings.firetrust.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAutoDiscoverySettings_FallsBackToFiretrust_WhenThunderbirdMethodsFail()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.StartsWith("https://emailsettings.firetrust.com/settings?q=", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse("""
                    {
                      "domain": "example.com",
                      "settings": [
                        {
                          "protocol": "IMAP",
                          "address": "imap.firetrust.example.com",
                          "port": 993,
                          "secure": "SSL",
                          "username": "user@example.com"
                        },
                        {
                          "protocol": "SMTP",
                          "address": "smtp.firetrust.example.com",
                          "port": 587,
                          "secure": "STARTTLS",
                          "username": "user@example.com"
                        }
                      ]
                    }
                    """, request);
            }

            if (uri.StartsWith("https://dns.google/resolve", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse("{\"Status\":0}", request);
            }

            return CreateStatusResponse(HttpStatusCode.NotFound, request);
        });

        using var client = new HttpClient(handler);
        var sut = new AutoDiscoveryService(client);

        var settings = await sut.GetAutoDiscoverySettings(new AutoDiscoveryMinimalSettings
        {
            Email = "user@example.com"
        });

        settings.Should().NotBeNull();
        settings!.GetImapSettings()!.Address.Should().Be("imap.firetrust.example.com");
        settings.GetSmptpSettings()!.Address.Should().Be("smtp.firetrust.example.com");
        handler.RequestedUris.Should().Contain(uri => uri.Contains("emailsettings.firetrust.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverCalDavServiceUriAsync_ReturnsKnownYahooEndpoint()
    {
        var sut = new AutoDiscoveryService(new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("No network call expected"))));

        var uri = await sut.DiscoverCalDavServiceUriAsync("user@yahoo.com");

        uri.Should().Be(new Uri("https://caldav.calendar.yahoo.com/"));
    }

    [Fact]
    public async Task DiscoverCalDavServiceUriAsync_ResolvesWellKnownRedirect()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.Equals("https://calendar.example.com/.well-known/caldav", StringComparison.OrdinalIgnoreCase))
            {
                var response = CreateStatusResponse(HttpStatusCode.Found, request);
                response.Headers.Location = new Uri("https://dav.example.net/caldav/");
                return response;
            }

            return CreateStatusResponse(HttpStatusCode.NotFound, request);
        });

        using var client = new HttpClient(handler);
        var sut = new AutoDiscoveryService(client);

        var uri = await sut.DiscoverCalDavServiceUriAsync("user@calendar.example.com");

        uri.Should().Be(new Uri("https://dav.example.net/caldav/"));
    }

    [Fact]
    public async Task GetAutoDiscoverySettings_ReturnsGuessedLocalhostSettings_ForManualLocalAccounts()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            if (uri.StartsWith("https://dns.google/resolve", StringComparison.OrdinalIgnoreCase))
            {
                return CreateJsonResponse("{\"Status\":0}", request);
            }

            return CreateStatusResponse(HttpStatusCode.NotFound, request);
        });

        using var client = new HttpClient(handler);
        var sut = new AutoDiscoveryService(client);

        var settings = await sut.GetAutoDiscoverySettings(new AutoDiscoveryMinimalSettings
        {
            Email = "user@localhost",
            DisplayName = "User",
            Password = "secret"
        });

        settings.Should().NotBeNull();
        settings!.Domain.Should().Be("localhost");
        settings.GetImapSettings()!.Address.Should().Be("localhost");
        settings.GetImapSettings()!.Port.Should().Be(993);
        settings.GetImapSettings()!.Username.Should().Be("user@localhost");
        settings.GetSmptpSettings()!.Address.Should().Be("localhost");
        settings.GetSmptpSettings()!.Port.Should().Be(587);
        settings.GetSmptpSettings()!.Username.Should().Be("user@localhost");
    }

    private static HttpResponseMessage CreateXmlResponse(string xml, HttpRequestMessage request)
        => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        };

    private static HttpResponseMessage CreateJsonResponse(string json, HttpRequestMessage request)
        => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage CreateStatusResponse(HttpStatusCode statusCode, HttpRequestMessage request)
        => new(statusCode)
        {
            RequestMessage = request
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            var response = _responseFactory(request);

            // Ensure probing logic sees a request URI even if response factory forgot it.
            response.RequestMessage ??= request;

            if (response.Headers.Date == null)
            {
                response.Headers.Date = DateTimeOffset.UtcNow;
            }

            if (!response.Headers.Contains("DAV") &&
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Headers.Add("DAV", "1, calendar-access");
            }

            var hasAllowHeader = response.Headers.TryGetValues("Allow", out var allowValues) &&
                                 allowValues.Any();

            if (!hasAllowHeader &&
                response.StatusCode == HttpStatusCode.OK &&
                request.Method == HttpMethod.Options)
            {
                response.Headers.Add("Allow", "PROPFIND");
            }

            if (response.Content == null)
            {
                response.Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain");
            }

            if (response.Content.Headers.ContentType == null)
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            }

            return Task.FromResult(response);
        }
    }
}
