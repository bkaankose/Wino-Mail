using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using global::Google.Apis.PeopleService.v1.Data;
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

    [Fact]
    public void UpdateContactRequest_UsesOnlyWritableFieldsInUpdateMask()
    {
        using var httpClient = new HttpClient();
        using var service = new PeopleServiceService(httpClient);
        var request = service.People.UpdateContact("people/123", new Person());

        using var message = request.CreateHttpRequestMessage();
        var query = message.RequestUri!.Query;
        var updateMask = query.Split('&')
            .Single(part => part.TrimStart('?').StartsWith("updatePersonFields=", StringComparison.Ordinal));
        var returnMask = query.Split('&')
            .Single(part => part.StartsWith("personFields=", StringComparison.Ordinal));

        updateMask.Should().NotContain("photos").And.NotContain("metadata").And.NotContain("fileAses");
        returnMask.Should().Contain("photos").And.Contain("metadata").And.Contain("fileAses");
    }

    [Fact]
    public async Task UpdateContactRequest_SerializesTheWritableFreeFormName()
    {
        using var httpClient = new HttpClient();
        using var service = new PeopleServiceService(httpClient);
        var person = new Person
        {
            Names = new Name[]
            {
                new()
                {
                    UnstructuredName = "Free-form 名前",
                    GivenName = "Structured",
                    FamilyName = "Name"
                }
            }
        };
        var request = service.People.UpdateContact("people/123", person);

        using var message = request.CreateHttpRequestMessage();
        var json = await message.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var name = document.RootElement.GetProperty("names")[0];

        name.GetProperty("unstructuredName").GetString().Should().Be("Free-form 名前");
        name.GetProperty("givenName").GetString().Should().Be("Structured");
        name.GetProperty("familyName").GetString().Should().Be("Name");
        name.TryGetProperty("displayName", out _).Should().BeFalse();
    }
}
