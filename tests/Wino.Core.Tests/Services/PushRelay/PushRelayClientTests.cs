using System.Net;
using FluentAssertions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Services.PushRelay;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class PushRelayClientTests
{
    private static readonly Guid DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SubscriptionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string DeviceJson = "{\"deviceId\":\"11111111-2222-3333-4444-555555555555\",\"platform\":\"ios\",\"pushTokenRegistered\":true,\"createdAt\":\"2026-10-13T00:00:00Z\",\"mailboxes\":[]}";
    private const string MailboxJson = "{\"id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"mailboxId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"provider\":\"imap\",\"identity\":\"u\",\"clientAccountId\":\"c\",\"status\":\"active\",\"createdAt\":\"2026-10-13T00:00:00Z\",\"muted\":true}";

    private readonly FakeTokenProvider _tokens = new();

    [Fact]
    public async Task EveryRouteSendsBearer_AndOnlyDeviceScopedRoutesSendDeviceId()
    {
        var handler = new RecordingHandler(request => request.Path switch
        {
            "/v1/devices" => RecordingHandler.Json(HttpStatusCode.Created, $"{{\"deviceId\":\"{DeviceId}\",\"serverTime\":\"2026-10-13T00:00:00Z\"}}"),
            "/v1/devices/me" when request.Method == HttpMethod.Delete => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/v1/devices/me" => RecordingHandler.Json(HttpStatusCode.OK, DeviceJson),
            "/v1/devices/me/accounts" => RecordingHandler.Json(HttpStatusCode.OK, "{\"removed\":0,\"mailboxes\":[]}"),
            "/v1/devices/me/push-test" => RecordingHandler.Json(HttpStatusCode.Accepted, "{\"status\":\"queued\"}"),
            "/v1/mailboxes" when request.Method == HttpMethod.Get => RecordingHandler.Json(HttpStatusCode.OK, "{\"items\":[]}"),
            "/.well-known/winorelay" => RecordingHandler.Json(HttpStatusCode.OK, "{\"version\":\"1\",\"apiVersion\":1}"),
            _ => RecordingHandler.Json(HttpStatusCode.OK, MailboxJson)
        });
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        await client.GetWellKnownAsync();
        await client.RegisterDeviceAsync(new PushRelayRegisterDeviceRequest("ios", null, "1.0", null, "en-US", "iOS 26", "Phone"));
        await client.GetDeviceAsync(DeviceId);
        await client.UpdateDeviceAsync(DeviceId, new PushRelayUpdateDeviceRequest("abc", null, null, null, null, null));
        await client.SyncAccountsAsync(DeviceId, ["a"]);
        await client.CreateMailboxAsync(DeviceId, new PushRelayCreateMailboxRequest("imap", "c", null));
        var updated = await client.UpdateMailboxCredentialsAsync(DeviceId, SubscriptionId, new PushRelayUpdateCredentialsRequest(null));
        await client.ListMailboxesAsync(DeviceId);
        await client.SendPushTestAsync(DeviceId);
        await client.DeleteDeviceAsync(DeviceId);

        updated.Muted.Should().BeTrue();
        handler.Requests.Should().OnlyContain(x => x.AuthScheme == "Bearer" && x.Bearer!.StartsWith("token-"));
        handler.Requests.Should().OnlyContain(x => x.Uri.Host == "relay.example.test");
        handler.Requests.Where(x => x.Path is "/v1/devices" or "/.well-known/winorelay").Should().OnlyContain(x => x.DeviceId == null);
        handler.Requests.Where(x => x.Path is not ("/v1/devices" or "/.well-known/winorelay"))
            .Should().HaveCount(8).And.OnlyContain(x => x.DeviceId == DeviceId.ToString("D"));
        handler.Requests.Single(x => x.Path == $"/v1/mailboxes/{SubscriptionId:D}/credentials").Method.Should().Be(HttpMethod.Put);
        handler.Requests.Single(x => x.Method == HttpMethod.Patch).Path.Should().Be("/v1/devices/me");
    }

    [Fact]
    public async Task RequestBodies_AreCamelCaseAndOmitNulls()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.Created, $"{{\"deviceId\":\"{DeviceId}\",\"serverTime\":\"2026-10-13T00:00:00Z\"}}"));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        await client.RegisterDeviceAsync(new PushRelayRegisterDeviceRequest("ios", null, "1.0", null, "en-US", "iOS 26", "Phone"));

        handler.Requests.Single().Body.Should().Be("{\"platform\":\"ios\",\"appVersion\":\"1.0\",\"locale\":\"en-US\",\"osVersion\":\"iOS 26\",\"name\":\"Phone\"}");
    }

    [Fact]
    public async Task ProblemDetails_AreParsed()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Problem(409, "mailbox_already_added", extra: $"\"subscriptionId\":\"{SubscriptionId}\""));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.CreateMailboxAsync(DeviceId, new PushRelayCreateMailboxRequest("imap", "c", null));

        var problem = (await act.Should().ThrowAsync<PushRelayApiException>()).Which.Problem;
        problem.Code.Should().Be(PushRelayErrorCodes.MailboxAlreadyAdded);
        problem.Status.Should().Be(409);
        problem.Detail.Should().Be("d");
        problem.SubscriptionId.Should().Be(SubscriptionId);
    }

    [Fact]
    public async Task RateLimitProblem_ReadsRetryAfterSeconds()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Problem(429, "rate_limited", extra: "\"retryAfterSeconds\":\"42\""));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.GetDeviceAsync(DeviceId);

        var ex = (await act.Should().ThrowAsync<PushRelayApiException>()).Which;
        ex.IsRateLimited.Should().BeTrue();
        ex.Problem.RetryAfterSeconds.Should().Be(42);
    }

    [Fact]
    public async Task ExpiredToken_IsRenewedAndRetriedOnce()
    {
        var calls = 0;
        var handler = new RecordingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? RecordingHandler.Problem(401, "unauthorized", PushRelayAuthReasons.TokenExpired)
            : RecordingHandler.Json(HttpStatusCode.OK, DeviceJson));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        await client.GetDeviceAsync(DeviceId);

        _tokens.InvalidateCount.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Bearer.Should().NotBe(handler.Requests[0].Bearer);
    }

    [Fact]
    public async Task ExpiredTokenTwice_IsNotRetriedAgain()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Problem(401, "unauthorized", PushRelayAuthReasons.TokenExpired));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.GetDeviceAsync(DeviceId);

        (await act.Should().ThrowAsync<PushRelayApiException>()).Which.IsTokenRejected.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeviceRevoked_IsSurfacedWithoutRetry()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Problem(401, "unauthorized", PushRelayAuthReasons.DeviceRevoked));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.GetDeviceAsync(DeviceId);

        (await act.Should().ThrowAsync<PushRelayApiException>()).Which.IsDeviceRevoked.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        _tokens.InvalidateCount.Should().Be(0);
    }

    [Fact]
    public async Task HtmlGatewayPage_IsServiceUnavailable()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.BadGateway, "<html><body>502</body></html>", "text/html"));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.GetDeviceAsync(DeviceId);

        var ex = (await act.Should().ThrowAsync<PushRelayApiException>()).Which;
        ex.Code.Should().Be(PushRelayErrorCodes.ServiceUnavailable);
        ex.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task NetworkFailure_IsServiceUnavailable()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("No connection could be made."));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));

        var act = () => client.GetDeviceAsync(DeviceId);

        (await act.Should().ThrowAsync<PushRelayApiException>()).Which.Code.Should().Be(PushRelayErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task CallerCancellation_Rethrows()
    {
        var handler = new RecordingHandler(_ => RecordingHandler.Json(HttpStatusCode.OK, DeviceJson));
        using var client = new PushRelayClient(_tokens, new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => client.GetDeviceAsync(DeviceId, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
