using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Google;
using Xunit;

namespace Wino.Core.Tests.Google;

public sealed class GoogleTasksClientTests
{
    [Fact]
    public async Task UpdateTaskAsync_IncludesTaskIdInPutBody()
    {
        var handler = new RecordingHandler();
        var client = new GoogleTasksClient(new HttpClient(handler));

        await client.UpdateTaskAsync(
            "list/id",
            "task/id",
            new GoogleTask { Title = "Updated" },
            "etag");

        handler.Method.Should().Be(HttpMethod.Put);
        handler.Uri.Should().Be("https://tasks.googleapis.com/tasks/v1/lists/list%2Fid/tasks/task%2Fid");
        handler.Body.Should().Contain("\"id\":\"task/id\"");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod Method { get; private set; }
        public string Uri { get; private set; }
        public string Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri?.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"task/id\",\"title\":\"Updated\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
