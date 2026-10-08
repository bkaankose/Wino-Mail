using System.Text.Json;
using FluentAssertions;
using Wino.Core.Google;
using Xunit;

namespace Wino.Core.Tests.Google;

/// <summary>
/// Pins the wire shape of the contact group calls: nothing here reaches Google.
/// </summary>
public sealed class GoogleContactGroupRequestTests
{
    private readonly PeopleServiceService _service = new(new HttpClient());

    [Fact]
    public void List_AsksForTheFieldsThatTellUserGroupsApart()
    {
        var request = _service.ContactGroups.List();
        request.PageToken = "next";

        using var message = request.CreateHttpRequestMessage();

        message.Method.Should().Be(HttpMethod.Get);
        message.RequestUri!.AbsolutePath.Should().Be("/v1/contactGroups");
        message.RequestUri.Query.Should().Contain("pageSize=1000").And.Contain("pageToken=next").And.Contain("groupType");
    }

    [Fact]
    public async Task Create_SendsTheNameInsideAContactGroup()
    {
        using var message = _service.ContactGroups.Create("Family").CreateHttpRequestMessage();

        message.Method.Should().Be(HttpMethod.Post);
        message.RequestUri!.AbsoluteUri.Should().Be("https://people.googleapis.com/v1/contactGroups");
        (await message.Content!.ReadAsStringAsync()).Should().Be("""{"contactGroup":{"name":"Family"}}""");
    }

    [Fact]
    public async Task Update_PutsOnlyTheNameToTheGroup()
    {
        using var message = _service.ContactGroups.Update("contactGroups/abc123", "Friends").CreateHttpRequestMessage();

        message.Method.Should().Be(HttpMethod.Put);
        message.RequestUri!.AbsoluteUri.Should().Be("https://people.googleapis.com/v1/contactGroups/abc123");
        (await message.Content!.ReadAsStringAsync()).Should().Be("""{"contactGroup":{"name":"Friends"},"updateGroupFields":"name"}""");
    }

    [Fact]
    public void Delete_TargetsTheGroupAndKeepsItsContacts()
    {
        using var message = _service.ContactGroups.Delete("contactGroups/abc123").CreateHttpRequestMessage();

        message.Method.Should().Be(HttpMethod.Delete);
        message.RequestUri!.AbsoluteUri.Should().Be("https://people.googleapis.com/v1/contactGroups/abc123");
    }

    [Fact]
    public async Task ModifyMembers_LeavesOutAnEmptySide()
    {
        using var message = _service.ContactGroups.ModifyMembers("contactGroups/abc123", ["people/c1"], []).CreateHttpRequestMessage();

        message.Method.Should().Be(HttpMethod.Post);
        message.RequestUri!.AbsoluteUri.Should().Be("https://people.googleapis.com/v1/contactGroups/abc123/members:modify");
        (await message.Content!.ReadAsStringAsync()).Should().Be("""{"resourceNamesToAdd":["people/c1"]}""");
    }

    [Fact]
    public void Responses_ReadGroupsAndMemberships()
    {
        const string groups =
            """
            {
              "contactGroups": [
                { "resourceName": "contactGroups/myContacts", "groupType": "SYSTEM_CONTACT_GROUP", "name": "myContacts" },
                { "resourceName": "contactGroups/abc123", "groupType": "USER_CONTACT_GROUP", "name": "Family", "metadata": { "deleted": false } }
              ],
              "nextPageToken": "next"
            }
            """;
        const string person =
            """
            {
              "resourceName": "people/c1",
              "memberships": [
                { "contactGroupMembership": { "contactGroupResourceName": "contactGroups/myContacts" } },
                { "contactGroupMembership": { "contactGroupResourceName": "contactGroups/abc123" } }
              ]
            }
            """;

        var listed = JsonSerializer.Deserialize(groups, GoogleApiJsonContext.Default.ListContactGroupsResponse);
        var read = JsonSerializer.Deserialize(person, GoogleApiJsonContext.Default.Person);

        listed!.NextPageToken.Should().Be("next");
        listed.ContactGroups.Select(group => (group.ResourceName, group.GroupType, group.Name)).Should().Equal(
            ("contactGroups/myContacts", "SYSTEM_CONTACT_GROUP", "myContacts"),
            ("contactGroups/abc123", "USER_CONTACT_GROUP", "Family"));
        read!.Memberships.Select(item => item.ContactGroupMembership.ContactGroupResourceName)
            .Should().Equal("contactGroups/myContacts", "contactGroups/abc123");
    }
}
