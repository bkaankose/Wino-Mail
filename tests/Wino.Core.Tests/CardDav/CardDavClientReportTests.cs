using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.CardDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class CardDavClientReportTests
{
    [Theory]
    [InlineData("https://contacts.example.test/books/alice%2Ftest.vcf")]
    [InlineData("/books/alice%2Ftest.vcf")]
    public async Task MultiGetAsync_UsesEscapedServerRelativeHrefs(string href)
    {
        const string bookHref = "https://contacts.example.test/books/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, body =>
            XDocument.Parse(body).Descendants(XName.Get("href", "DAV:")).Any(element => element.Value.StartsWith("https://", StringComparison.Ordinal))
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : MultiStatus(SyncResponse(bookHref)));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        await client.MultiGetAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, new[] { href });

        var requested = XDocument.Parse(requests.Single().Body).Descendants(XName.Get("href", "DAV:")).Single().Value;
        requested.Should().Be("/books/alice%2Ftest.vcf");
    }

    [Fact]
    public async Task SyncCollectionAsync_CollectionHrefOmitsTrailingSlash_ExcludesItAndPreservesTruncation()
    {
        const string bookHref = "https://contacts.example.test/books/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, _ => MultiStatus(SyncResponse(bookHref).Replace(
            "<D:sync-token>", "<D:response><D:href>/books</D:href><D:status>HTTP/1.1 507 Insufficient Storage</D:status></D:response><D:sync-token>")));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        var page = await client.SyncCollectionAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, null, 0);

        page.Changes.Should().ContainSingle().Which.ExactHref.Should().Be(bookHref + "alice.vcf");
        page.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task SyncCollectionAsync_RequestsOnlyWebDavProperties()
    {
        const string bookHref = "https://contacts.example.test/books/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, _ => MultiStatus(SyncResponse(bookHref)));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        await client.SyncCollectionAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, null, 250);

        XNamespace dav = "DAV:";
        var properties = XDocument.Parse(requests.Single().Body).Root!.Element(dav + "prop")!;
        properties.Elements().Select(element => element.Name).Should().Equal(dav + "getetag");
    }

    [Theory]
    [InlineData("https://contacts.example.test/books/alice.vcf")]
    [InlineData("/books/alice.vcf")]
    public async Task SyncCollectionAsync_MissingPropertyDoesNotDeleteResource(string href)
    {
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, _ => MultiStatus($$"""
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:response><D:href>{{href}}</D:href>
                <D:propstat><D:prop><D:getetag>"1"</D:getetag></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
                <D:propstat><D:prop><C:address-data /></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>
              </D:response><D:sync-token>next</D:sync-token>
            </D:multistatus>
            """));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        var page = await client.SyncCollectionAsync(Settings,
            new CardDavAddressBook { ExactHref = "https://contacts.example.test/books/" }, null, 0);

        var change = page.Changes.Should().ContainSingle().Subject;
        change.IsDeleted.Should().BeFalse();
        change.StatusCode.Should().Be(200);
        change.ETag.Should().Be("\"1\"");
        change.ExactHref.Should().Be("https://contacts.example.test/books/alice.vcf");
    }

    [Fact]
    public async Task SyncCollectionAsync_ServerRejectsLimit_RetriesWithoutLimitAndRemembersOrigin()
    {
        // Unique per test: the client remembers limit-rejecting origins for the process.
        var bookHref = $"https://{Guid.NewGuid():N}.example.test/addressbooks/user/card/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, body => body.Contains("<D:limit>", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : MultiStatus(SyncResponse(bookHref)));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        var page = await client.SyncCollectionAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, null, 250);
        await client.SyncCollectionAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, "next-token", 250);

        page.NextSyncToken.Should().Be("next-token");
        page.Changes.Should().ContainSingle().Which.ExactHref.Should().Be(bookHref + "alice.vcf");
        requests.Select(request => request.Body.Contains("<D:limit>", StringComparison.Ordinal))
            .Should().Equal(true, false, false);
        requests.Should().OnlyContain(request => request.Method == "REPORT" && request.Depth == "0");
    }

    [Fact]
    public async Task SyncCollectionAsync_InvalidSyncToken_IsNotRetriedWithoutLimit()
    {
        var bookHref = $"https://{Guid.NewGuid():N}.example.test/addressbooks/user/card/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""<D:error xmlns:D="DAV:"><D:valid-sync-token /></D:error>""", Encoding.UTF8, "application/xml")
        });
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        var act = () => client.SyncCollectionAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, "stale", 250);

        (await act.Should().ThrowAsync<DavRequestException>()).Which.HasError("valid-sync-token").Should().BeTrue();
        requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MultiGetAsync_SendsDepthZero()
    {
        var bookHref = "https://contacts.example.test/addressbooks/user/card/";
        var requests = new List<(string Method, string Depth, string Body)>();
        var transport = CaptureTransport(requests, _ => MultiStatus(SyncResponse(bookHref)));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        await client.MultiGetAsync(Settings, new CardDavAddressBook { ExactHref = bookHref }, [bookHref + "alice.vcf"]);

        requests.Should().ContainSingle().Which.Depth.Should().Be("0");
    }

    private static Mock<IDavTransport> CaptureTransport(
        List<(string Method, string Depth, string Body)> requests,
        Func<string, HttpResponseMessage> respond)
    {
        var transport = new Mock<IDavTransport>();
        transport.Setup(item => item.SendAsync(
                It.IsAny<HttpRequestMessage>(),
                It.IsAny<DavAuthenticationProfile>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (HttpRequestMessage request, DavAuthenticationProfile _, CancellationToken cancellationToken) =>
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                var depth = request.Headers.TryGetValues("Depth", out var values) ? values.Single() : null;
                requests.Add((request.Method.Method, depth, body));
                var response = respond(body);
                response.RequestMessage = request;
                return response;
            });
        return transport;
    }

    private static CardDavConnectionSettings Settings => new()
    {
        ServiceUri = new Uri("https://contacts.example.test/"),
        AccountAddress = "user@example.test",
        Authentication = new DavAuthenticationProfile
        {
            Kind = DavAuthenticationKind.Basic,
            Username = "user@example.test",
            Password = "app-password"
        }
    };

    private static HttpResponseMessage MultiStatus(string xml) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml")
    };

    private static string SyncResponse(string bookHref) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
          <D:response>
            <D:href>{{bookHref}}alice.vcf</D:href>
            <D:propstat>
              <D:prop><D:getetag>"1"</D:getetag></D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:sync-token>next-token</D:sync-token>
        </D:multistatus>
        """;
}
