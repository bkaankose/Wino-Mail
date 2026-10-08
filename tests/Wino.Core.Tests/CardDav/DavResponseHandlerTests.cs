using System.Net;
using System.Net.Http;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class DavResponseHandlerTests
{
    [Theory]
    [InlineData(401, "GET")]
    [InlineData(403, "MKCOL")]
    [InlineData(403, "PUT")]
    [InlineData(404, "GET")]
    [InlineData(409, "PUT")]
    [InlineData(412, "PUT")]
    [InlineData(423, "PUT")]
    [InlineData(429, "REPORT")]
    [InlineData(500, "REPORT")]
    public async Task EnsureSuccessAsync_MapsDavStatusByOperation(int statusCode, string method)
    {
        var handler = new DavResponseHandler();
        using var response = new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            RequestMessage = new HttpRequestMessage(new HttpMethod(method), "https://dav.example.test/resource"),
            Content = new StringContent("<D:error xmlns:D=\"DAV:\"><D:need-privileges /></D:error>")
        };

        var action = () => handler.EnsureSuccessAsync(response);
        var exception = await action.Should().ThrowAsync<DavRequestException>();

        exception.Which.StatusCode.Should().Be(statusCode);
        exception.Which.Message.Should().NotBeNullOrWhiteSpace();
        if (statusCode == 403 && method == "MKCOL")
            exception.Which.Message.Should().Be(Translator.DavError_CollectionCreationDenied);
    }

    [Fact]
    public async Task EnsureSuccessAsync_PreservesRetryAfter()
    {
        var handler = new DavResponseHandler();
        using var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://dav.example.test/resource")
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var action = () => handler.EnsureSuccessAsync(response);
        var exception = await action.Should().ThrowAsync<DavRequestException>();

        exception.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }
}
