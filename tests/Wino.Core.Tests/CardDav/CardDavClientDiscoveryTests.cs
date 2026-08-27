using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.CardDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class CardDavClientDiscoveryTests
{
    [Theory]
    [InlineData("<D:privilege><D:bind /></D:privilege>", true)]
    [InlineData("<D:privilege><D:read /></D:privilege>", false)]
    [InlineData("", false)]
    public async Task DiscoverAsync_UsesHomeSetBindPrivilegeForCollectionCreation(
        string privilegeXml,
        bool expected)
    {
        var transport = new Mock<IDavTransport>();
        transport.SetupSequence(item => item.SendAsync(
                It.IsAny<HttpRequestMessage>(),
                It.IsAny<DavAuthenticationProfile>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MultiStatus(ContextResponse))
            .ReturnsAsync(MultiStatus(HomeResponse(privilegeXml)));
        var client = new CardDavClient(transport.Object, new DavMultistatusReader());

        var discovery = await client.DiscoverAsync(new CardDavConnectionSettings
        {
            ServiceUri = new Uri("https://contacts.example.test/"),
            AccountAddress = "user@example.test",
            Authentication = new DavAuthenticationProfile
            {
                Kind = DavAuthenticationKind.Basic,
                Username = "user@example.test",
                Password = "app-password"
            }
        });

        discovery.SupportsAddressBookCreation.Should().Be(expected);
        discovery.AddressBooks.Should().ContainSingle();
    }

    private static HttpResponseMessage MultiStatus(string xml) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml")
    };

    private const string ContextResponse = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
          <D:response>
            <D:href>/</D:href>
            <D:propstat>
              <D:prop>
                <D:current-user-principal><D:href>/principals/user/</D:href></D:current-user-principal>
                <C:addressbook-home-set><D:href>/address-books/user/</D:href></C:addressbook-home-set>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;

    private static string HomeResponse(string privilegeXml) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
          <D:response>
            <D:href>/address-books/user/</D:href>
            <D:propstat>
              <D:prop>
                <D:resourcetype><D:collection /></D:resourcetype>
                <D:current-user-privilege-set>{{privilegeXml}}</D:current-user-privilege-set>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:response>
            <D:href>/address-books/user/default/</D:href>
            <D:propstat>
              <D:prop>
                <D:displayname>Address book</D:displayname>
                <D:resourcetype><D:collection /><C:addressbook /></D:resourcetype>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;
}
