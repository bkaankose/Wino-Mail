using System.Net.Http;
using FluentAssertions;
using Wino.Core.Google;
using Xunit;

namespace Wino.Core.Tests.Google;

public class PeopleConnectionsRequestTests
{
    [Fact]
    public void DeltaConnectionsRequest_AlwaysAsksForTheNextSyncToken()
    {
        using var httpClient = new HttpClient();
        using var service = new PeopleServiceService(httpClient);
        var request = service.Connections.List("people/me");
        request.PersonFields = "names,emailAddresses";
        request.SyncToken = "existing-token";

        using var message = request.CreateHttpRequestMessage();

        message.RequestUri!.Query.Should().Contain("syncToken=existing-token").And.Contain("requestSyncToken=true");
    }

    [Fact]
    public void FullConnectionsRequest_AsksForASyncTokenWithoutSendingOne()
    {
        using var httpClient = new HttpClient();
        using var service = new PeopleServiceService(httpClient);
        var request = service.Connections.List("people/me");
        request.PersonFields = "names,emailAddresses";

        using var message = request.CreateHttpRequestMessage();

        message.RequestUri!.Query.Should().Contain("requestSyncToken=true");
        message.RequestUri.Query.Should().NotContain("&syncToken=").And.NotContain("?syncToken=");
    }
}
