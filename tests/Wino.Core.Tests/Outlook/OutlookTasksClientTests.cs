using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Moq;
using Wino.Core.Outlook;
using Xunit;

namespace Wino.Core.Tests.Outlook;

public class OutlookTasksClientTests
{
    [Fact]
    public async Task GetTasksDeltaAsync_UsesDeltaFunctionSyntaxForInitialRequest()
    {
        var (client, request) = CreateClient();

        await client.GetTasksDeltaAsync("list/id");

        request().URI.AbsoluteUri.Should().Be(
            "https://graph.microsoft.com/v1.0/me/todo/lists/list%2Fid/tasks/delta()?$expand=checklistItems&$top=100");
    }

    [Fact]
    public async Task GetTasksDeltaAsync_NormalizesServiceDeltaLinkAndPreservesToken()
    {
        var (client, request) = CreateClient();
        const string deltaLink = "https://graph.microsoft.com/v1.0/me/todo/lists/list-id/tasks/delta?$deltatoken=opaque-token";

        await client.GetTasksDeltaAsync("list-id", deltaLink);

        request().URI.AbsoluteUri.Should().Be(
            "https://graph.microsoft.com/v1.0/me/todo/lists/list-id/tasks/delta()?$deltatoken=opaque-token");
    }

    [Fact]
    public async Task GetTaskListsDeltaAsync_DoesNotSendUnsupportedTopQuery()
    {
        var (client, request) = CreateClient<OutlookTaskListCollectionResponse>();

        await client.GetTaskListsDeltaAsync();

        request().URI.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/me/todo/lists/delta");
    }

    private static (OutlookTasksClient Client, Func<RequestInformation> Request) CreateClient()
        => CreateClient<OutlookTaskCollectionResponse>();

    private static (OutlookTasksClient Client, Func<RequestInformation> Request) CreateClient<T>() where T : IParsable, new()
    {
        RequestInformation capturedRequest = null;
        var adapter = new Mock<IRequestAdapter>();
        adapter.Setup(value => value.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<T>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<RequestInformation, ParsableFactory<T>, Dictionary<string, ParsableFactory<IParsable>>, CancellationToken>(
                (request, _, _, _) => capturedRequest = request)
            .ReturnsAsync(new T());

        return (new OutlookTasksClient(adapter.Object), () => capturedRequest);
    }
}
