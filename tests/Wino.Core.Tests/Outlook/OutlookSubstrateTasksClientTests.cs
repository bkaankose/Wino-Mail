using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Wino.Core.Outlook;
using Xunit;

namespace Wino.Core.Tests.Outlook;

public sealed class OutlookSubstrateTasksClientTests
{
    [Fact]
    public async Task Mutations_UseObservedMethodsBodiesAndConcurrencyHeaders()
    {
        var handler = new RecordingHandler(
            """{"Id":"group-id","ChangeKey":"group-v1","Name":"Group","OrderDateTime":"2026-08-28T10:00:00Z"}""",
            """{"Id":"group-id","ChangeKey":"group-v2","Name":"Renamed","OrderDateTime":"2026-08-28T10:00:01Z"}""",
            """{"Id":"list-id","ChangeKey":"list-v2","ParentFolderGroupId":"group-id","OrderDateTime":"2026-08-28T10:00:02Z"}""",
            null);
        var client = new OutlookSubstrateTasksClient(new HttpClient(handler), () => Task.FromResult(CreateToken()));

        await client.CreateFolderGroupAsync("Group", DateTimeOffset.Parse("2026-08-28T10:00:00Z"));
        await client.UpdateFolderGroupAsync("group-id", "Renamed", DateTimeOffset.Parse("2026-08-28T10:00:01Z"), "group-v1");
        await client.UpdateTaskFolderAsync("list-id", "group-id", DateTimeOffset.Parse("2026-08-28T10:00:02Z"), "list-v1");
        await client.DeleteFolderGroupAsync("group-id", "group-v2");

        handler.Requests.Should().HaveCount(4);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Path.Should().Be("/todob2/api/v1/foldergroups");
        handler.Requests[0].Body.Should().Contain("\"Name\":\"Group\"").And.Contain("\"OrderDateTime\":");
        handler.Requests[0].IfMatch.Should().BeNull();

        handler.Requests[1].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[1].Path.Should().Be("/todob2/api/v1/foldergroups/group-id");
        handler.Requests[1].IfMatch.Should().Be("group-v1");
        handler.Requests[1].Body.Should().Contain("\"Name\":\"Renamed\"");

        handler.Requests[2].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[2].Path.Should().Be("/todob2/api/v1/taskfolders/list-id");
        handler.Requests[2].IfMatch.Should().Be("list-v1");
        handler.Requests[2].Body.Should().Contain("\"ParentFolderGroupId\":\"group-id\"");

        handler.Requests[3].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[3].Path.Should().Be("/todob2/api/v1/foldergroups/group-id");
        handler.Requests[3].IfMatch.Should().Be("group-v2");

        handler.Requests.Should().OnlyContain(request => request.ContentType == "application/json" || request.Method == HttpMethod.Delete);
        handler.Requests.Should().OnlyContain(request => !string.IsNullOrWhiteSpace(request.RequestId));
        handler.Requests.Should().OnlyContain(request => request.AnchorMailbox == "OID:user-id@tenant-id");
        handler.Requests.Should().OnlyContain(request => request.Authorization != null && request.Authorization.StartsWith("Bearer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredAbsoluteDeltaLink_IsReportedForOneTimeFullRetry()
    {
        var handler = new RecordingHandler(statusCode: HttpStatusCode.Gone);
        var client = new OutlookSubstrateTasksClient(new HttpClient(handler), () => Task.FromResult(CreateToken()));

        var action = () => client.GetFolderGroupsAsync("https://outlook.office.com/todob2/api/v1/foldergroups?deltatoken=expired");

        await action.Should().ThrowAsync<SubstrateDeltaCursorInvalidException>();
        handler.Requests.Should().ContainSingle();
    }

    private static string CreateToken()
    {
        static string Part(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("{\"alg\":\"none\"}")}.{Part("{\"oid\":\"user-id\",\"tid\":\"tenant-id\"}")}.token";
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(params string[] responses) : this(HttpStatusCode.OK, responses) { }

        public RecordingHandler(HttpStatusCode statusCode, params string[] responses)
        {
            _statusCode = statusCode;
            _responses = new Queue<string>(responses ?? []);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.TryGetValues("If-Match", out var ifMatch) ? ifMatch.Single() : null,
                request.Headers.TryGetValues("X-Todo-Request-Id", out var requestId) ? requestId.Single() : null,
                request.Headers.TryGetValues("X-AnchorMailbox", out var anchor) ? anchor.Single() : null,
                request.Headers.Authorization?.ToString()));

            var body = _responses.Count > 0 ? _responses.Dequeue() : null;
            return new HttpResponseMessage(_statusCode)
            {
                Content = body is null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string Body,
        string ContentType,
        string IfMatch,
        string RequestId,
        string AnchorMailbox,
        string Authorization);
}
