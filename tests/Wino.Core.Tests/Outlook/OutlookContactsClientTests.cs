using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Graph.Models;
using Moq;
using Wino.Core.Outlook;
using Xunit;

namespace Wino.Core.Tests.Outlook;

public class OutlookContactsClientTests
{
    [Fact]
    public async Task GetDefaultContactsAsync_UsesPagedListEndpointInsteadOfUnsupportedDeltaRoute()
    {
        var (client, request) = CreateClient();

        await client.GetDefaultContactsAsync();

        request().URI.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/me/contacts?$top=100");
        request().URI.AbsoluteUri.Should().NotContain("/delta");
    }

    [Fact]
    public async Task GetDeltaAsync_UsesFolderDeltaEndpointAndPreferPageSize()
    {
        var (client, request) = CreateClient();

        await client.GetDeltaAsync("folder/id");

        request().URI.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/me/contactFolders/folder%2Fid/contacts/delta");
        request().URI.Query.Should().BeEmpty();
        request().Headers.TryGetValue("Prefer", out var values).Should().BeTrue();
        values.Should().Contain("IdType=\"ImmutableId\"").And.Contain("odata.maxpagesize=100");
    }

    [Fact]
    public async Task GetPhotoAsync_ReadsRawImageStream()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xD9];
        RequestInformation capturedRequest = null;
        var adapter = new Mock<IRequestAdapter>();
        adapter.Setup(value => value.SendPrimitiveAsync<Stream>(
                It.IsAny<RequestInformation>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestInformation, Dictionary<string, ParsableFactory<IParsable>>, CancellationToken>(
                (request, _, _) => capturedRequest = request)
            .ReturnsAsync(new MemoryStream(jpegBytes));

        var result = await new OutlookContactsClient(adapter.Object).GetPhotoAsync("contact/id");

        result.Should().Equal(jpegBytes);
        capturedRequest.URI.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/me/contacts/contact%2Fid/photo/$value");
        capturedRequest.HttpMethod.Should().Be(Method.GET);
    }

    private static (OutlookContactsClient Client, Func<RequestInformation> Request) CreateClient()
    {
        RequestInformation capturedRequest = null;
        var adapter = new Mock<IRequestAdapter>();
        adapter.Setup(value => value.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<OutlookContactCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestInformation, ParsableFactory<OutlookContactCollectionResponse>, Dictionary<string, ParsableFactory<IParsable>>, CancellationToken>(
                (request, _, _, _) => capturedRequest = request)
            .ReturnsAsync(new OutlookContactCollectionResponse());

        return (new OutlookContactsClient(adapter.Object), () => capturedRequest);
    }
}
