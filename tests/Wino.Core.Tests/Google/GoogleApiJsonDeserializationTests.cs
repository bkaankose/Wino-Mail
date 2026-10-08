using System.Text.Json;
using FluentAssertions;
using Wino.Core.Google;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Google;

public sealed class GoogleApiJsonDeserializationTests
{
    [Fact]
    public void Profile_ReadsGmailHistoryIdEncodedAsString()
    {
        const string json =
            """
            {
              "emailAddress": "user@example.com",
              "messagesTotal": 42,
              "threadsTotal": 21,
              "historyId": "987654321012345678"
            }
            """;

        var profile = JsonSerializer.Deserialize(json, GoogleApiJsonContext.Default.Profile);

        profile.Should().NotBeNull();
        profile!.HistoryId.Should().Be(987654321012345678UL);
    }

    [Fact]
    public void Message_ReadsGmailInt64FieldsEncodedAsStrings()
    {
        const string json =
            """
            {
              "id": "message-id",
              "threadId": "thread-id",
              "historyId": "987654321012345678",
              "internalDate": "1721930400123"
            }
            """;

        var message = JsonSerializer.Deserialize(json, GmailSynchronizerJsonContext.Default.Message);

        message.Should().NotBeNull();
        message!.HistoryId.Should().Be(987654321012345678UL);
        message.InternalDate.Should().Be(1721930400123L);
    }

    [Fact]
    public void HistoryResponse_ReadsGmailHistoryIdsEncodedAsStrings()
    {
        const string json =
            """
            {
              "history": [
                {
                  "id": "987654321012345677"
                }
              ],
              "historyId": "987654321012345678"
            }
            """;

        var response = JsonSerializer.Deserialize(json, GoogleApiJsonContext.Default.ListHistoryResponse);

        response.Should().NotBeNull();
        response!.HistoryId.Should().Be(987654321012345678UL);
        response.History.Should().ContainSingle()
            .Which.Id.Should().Be(987654321012345677UL);
    }
}
