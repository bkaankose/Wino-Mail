using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// RFC 6764 discovery in <see cref="AutoDiscoveryService"/>: SRV and TXT records answered by a fake
/// DNS-over-HTTPS endpoint, and endpoint probes answered by a fake server.
/// </summary>
public sealed class AutoDiscoveryCalDavTests
{
    [Fact]
    public async Task SecureSrvRecord_WithTxtPath_IsProbedBeforeWellKnown()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_caldavs._tcp.example.com|SRV"] = ["0 0 443 dav.example.com."];
        handler.Dns["_caldavs._tcp.example.com|TXT"] = ["\"path=/dav/calendars/\""];
        handler.Probes["https://dav.example.com/dav/calendars/"] = HttpStatusCode.Unauthorized;
        handler.Probes["https://example.com/.well-known/caldav"] = HttpStatusCode.Unauthorized;

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@example.com");

        result.Should().Be(new Uri("https://dav.example.com/dav/calendars/"));
        handler.ProbedUris.Should().NotContain("https://example.com/.well-known/caldav");
    }

    [Fact]
    public async Task SecureSrvRecord_WithoutTxt_UsesTargetWellKnown_AndFollowsRedirect()
    {
        // iCloud: _caldavs._tcp.icloud.com → 0 0 443 caldav.icloud.com.
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_caldavs._tcp.icloud.com|SRV"] = ["0 0 443 caldav.icloud.com."];
        handler.Redirects["https://caldav.icloud.com/.well-known/caldav"] = "https://caldav.icloud.com/";

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@icloud.com");

        result.Should().Be(new Uri("https://caldav.icloud.com/"));
        handler.DnsQueries.Should().Equal("_caldavs._tcp.icloud.com|SRV", "_caldavs._tcp.icloud.com|TXT");
    }

    [Fact]
    public async Task SrvTargets_AreTriedByPriorityThenWeight_WithNonDefaultPort()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_caldavs._tcp.example.com|SRV"] = ["10 0 443 backup.example.com.", "0 1 443 light.example.com.", "0 5 8443 primary.example.com."];
        handler.Probes["https://backup.example.com/.well-known/caldav"] = HttpStatusCode.MultiStatus;

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@example.com");

        result.Should().Be(new Uri("https://backup.example.com/.well-known/caldav"));
        handler.ProbedUris.Distinct().Should().Equal(
            "https://primary.example.com:8443/.well-known/caldav",
            "https://light.example.com/.well-known/caldav",
            "https://backup.example.com/.well-known/caldav");
    }

    [Fact]
    public async Task PlainSrvRecord_IsTriedOverHttpsFirst_AndOverHttpOnlyAfterEverySecureCandidate()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_caldav._tcp.example.com|SRV"] = ["0 0 80 dav.example.com."];
        handler.Dns["_caldav._tcp.example.com|TXT"] = ["path=/cal/"];
        handler.Probes["http://dav.example.com/cal/"] = HttpStatusCode.Unauthorized;

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@example.com");

        result.Should().Be(new Uri("http://dav.example.com/cal/"));
        var probes = handler.ProbedUris.Distinct().ToList();
        probes.First().Should().Be("https://dav.example.com/cal/");
        probes.Last().Should().Be("http://dav.example.com/cal/");
        probes.Should().Contain("https://example.com/.well-known/caldav").And.Contain("https://caldav.example.com/");
    }

    [Fact]
    public async Task SecureSrvRecord_HidesThePlainRecord()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_caldavs._tcp.example.com|SRV"] = ["0 0 443 secure.example.com."];
        handler.Dns["_caldav._tcp.example.com|SRV"] = ["0 0 80 plain.example.com."];

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@example.com");

        result.Should().BeNull();
        handler.ProbedUris.Should().NotContain(uri => uri.Contains("plain.example.com"));
        handler.DnsQueries.Should().NotContain("_caldav._tcp.example.com|SRV");
    }

    [Fact]
    public async Task WithoutSrv_FallsBackToWellKnown_AndCachesTheResult()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Probes["https://example.com/.well-known/caldav"] = HttpStatusCode.Unauthorized;
        var service = new AutoDiscoveryService(new HttpClient(handler));

        var first = await service.DiscoverCalDavServiceUriAsync("user@example.com");
        var requestsAfterFirst = handler.RequestCount;
        var second = await service.DiscoverCalDavServiceUriAsync("other@example.com");

        first.Should().Be(new Uri("https://example.com/.well-known/caldav"));
        second.Should().Be(first);
        handler.RequestCount.Should().Be(requestsAfterFirst);
    }

    [Fact]
    public async Task SrvOfTheMxDomain_IsTheLastSecureResort()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["example.org|MX"] = ["10 mx1.hoster.net."];
        handler.Dns["_caldavs._tcp.hoster.net|SRV"] = ["0 0 443 dav.hoster.net."];
        handler.Probes["https://dav.hoster.net/.well-known/caldav"] = HttpStatusCode.Unauthorized;

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@example.org");

        result.Should().Be(new Uri("https://dav.hoster.net/.well-known/caldav"));
        handler.ProbedUris.Should().Contain("https://example.org/.well-known/caldav", "the domain's own candidates come first");
    }

    [Fact]
    public async Task NothingPublished_ReturnsNull()
    {
        // adastria.ws: no SRV, no well-known, MX at the hoster without SRV.
        var handler = new FakeDiscoveryHandler();
        handler.Dns["adastria.ws|MX"] = ["1 mx1.mail.ovh.net."];

        var result = await new AutoDiscoveryService(new HttpClient(handler)).DiscoverCalDavServiceUriAsync("user@adastria.ws");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CardDav_UsesItsOwnSrvLabels_AndCache()
    {
        var handler = new FakeDiscoveryHandler();
        handler.Dns["_carddavs._tcp.example.com|SRV"] = ["0 0 443 contacts.example.com."];
        handler.Dns["_caldavs._tcp.example.com|SRV"] = ["0 0 443 calendar.example.com."];
        handler.Probes["https://contacts.example.com/.well-known/carddav"] = HttpStatusCode.Unauthorized;
        handler.Probes["https://calendar.example.com/.well-known/caldav"] = HttpStatusCode.Unauthorized;
        var service = new AutoDiscoveryService(new HttpClient(handler));

        var cardDav = await service.DiscoverCardDavServiceUriAsync("user@example.com");
        var calDav = await service.DiscoverCalDavServiceUriAsync("user@example.com");

        cardDav.Should().Be(new Uri("https://contacts.example.com/.well-known/carddav"));
        calDav.Should().Be(new Uri("https://calendar.example.com/.well-known/caldav"));
    }

    [Theory]
    [InlineData("path=/caldav/", "/caldav/")]
    [InlineData("\"path=/dav/\"", "/dav/")]
    [InlineData("\"txtvers=1\" \"path=/a/b\"", "/a/b")]
    [InlineData("path=relative", null)]
    [InlineData("path=//evil.example/", null)]
    [InlineData("v=spf1 -all", null)]
    public void ParseTxtPath_ReadsTheContextPath(string record, string? expected)
        => AutoDiscoveryService.ParseTxtPath([record]).Should().Be(expected);

    private sealed class FakeDiscoveryHandler : HttpMessageHandler
    {
        public Dictionary<string, string[]> Dns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HttpStatusCode> Probes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
        public List<string> DnsQueries { get; } = [];
        public List<string> ProbedUris { get; } = [];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var uri = request.RequestUri!;

            if (uri.Host == "dns.google")
                return Task.FromResult(AnswerDns(uri));

            var key = uri.AbsoluteUri;
            ProbedUris.Add(key);

            if (Redirects.TryGetValue(key, out var location))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently) { RequestMessage = request };
                redirect.Headers.Location = new Uri(location);
                return Task.FromResult(redirect);
            }

            var status = Probes.TryGetValue(key, out var configured) ? configured : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }

        private HttpResponseMessage AnswerDns(Uri uri)
        {
            var query = uri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            var key = $"{query["name"]}|{query["type"]}";
            DnsQueries.Add(key);

            var json = Dns.TryGetValue(key, out var data)
                ? JsonSerializer.Serialize(new { Status = 0, Answer = data.Select(value => new { name = query["name"] + ".", data = value }).ToArray() })
                : "{\"Status\":3}";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
