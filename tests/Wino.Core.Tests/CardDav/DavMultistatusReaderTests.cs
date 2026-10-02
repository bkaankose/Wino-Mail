using System.Text;
using FluentAssertions;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class DavMultistatusReaderTests
{
    [Theory]
    [InlineData("<html><body>Server error</body></html>")]
    [InlineData("<multistatus xmlns='urn:wrong-namespace' />")]
    public async Task ReadAsync_NonDavDocument_RejectsInsteadOfReturningEmptyListing(string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var action = () => new DavMultistatusReader().ReadAsync(stream);

        await action.Should().ThrowAsync<System.Xml.XmlException>();
    }

    [Fact]
    public async Task ReadAsync_CompactXmlWithTokenBeforeResponse_PreservesResponse()
    {
        const string xml = "<D:multistatus xmlns:D='DAV:'><D:sync-token>next</D:sync-token><D:response><D:href>/books/alice.vcf</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response></D:multistatus>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = await new DavMultistatusReader().ReadAsync(stream);

        result.SyncToken.Should().Be("next");
        result.Responses.Should().ContainSingle().Which.Href.Should().Be("/books/alice.vcf");
    }

    [Fact]
    public async Task ReadAsync_PreservesMixedResponseAndPropstatStatuses()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:response>
                <D:href>/books/a%2Fb.vcf</D:href>
                <D:propstat><D:prop><D:getetag>&quot;one&quot;</D:getetag></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
                <D:propstat><D:prop><C:address-data /></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>
              </D:response>
              <D:response><D:href>/books/gone.vcf</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>
              <D:sync-token>opaque-token</D:sync-token>
            </D:multistatus>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = await new DavMultistatusReader().ReadAsync(stream);

        result.SyncToken.Should().Be("opaque-token");
        result.Responses.Should().HaveCount(2);
        result.Responses[0].Href.Should().Be("/books/a%2Fb.vcf");
        result.Responses[0].PropertyStatuses.Select(item => item.StatusCode).Should().Equal(200, 404);
        result.Responses[1].StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ReadAsync_RejectsDocumentTypeDeclarations()
    {
        const string xml = "<!DOCTYPE multistatus [<!ENTITY xxe SYSTEM 'file:///windows/win.ini'>]><multistatus xmlns='DAV:'><response><href>&xxe;</href></response></multistatus>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var action = () => new DavMultistatusReader().ReadAsync(stream);

        await action.Should().ThrowAsync<System.Xml.XmlException>();
    }
}
