using System.Net;
using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Services.PushRelay;

namespace Wino.Core.Tests.Services.PushRelay;

/// <summary>Records each request with its body and answers from a callback.</summary>
internal sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("Request without URI."),
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues(PushRelayClient.DeviceIdHeaderName, out var values) ? values.Single() : null,
            body);
        lock (Requests) Requests.Add(recorded);
        return respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json")
        => new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Problem(int status, string code, string? reason = null, string? extra = null)
    {
        var reasonPart = reason is null ? string.Empty : $",\"reason\":\"{reason}\"";
        var extraPart = extra is null ? string.Empty : "," + extra;
        return Json((HttpStatusCode)status,
            $"{{\"type\":\"urn:winorelay:error:{code}\",\"title\":\"t\",\"status\":{status},\"detail\":\"d\"{reasonPart}{extraPart}}}",
            "application/problem+json");
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? AuthScheme, string? Bearer, string? DeviceId, string? Body)
{
    public string Path => Uri.AbsolutePath;
}

internal sealed class FakeTokenProvider : IPushRelayTokenProvider
{
    private int _issued;

    public int InvalidateCount { get; private set; }
    public Exception? Failure { get; set; }
    public int? MailboxLimit { get; set; } = 10;
    public Uri BaseUri { get; set; } = new("https://relay.example.test/");

    public Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
            return Task.FromException<PushRelayAccessToken>(Failure);

        var number = Interlocked.Increment(ref _issued);
        return Task.FromResult(new PushRelayAccessToken($"token-{number}", DateTimeOffset.UtcNow.AddMinutes(30), BaseUri, MailboxLimit));
    }

    public void Invalidate() => InvalidateCount++;
}

internal sealed class InMemoryIdentityStore(List<string>? log = null) : IPushRelayDeviceIdentityStore
{
    public PushRelayDeviceIdentity? Identity { get; set; }
    public List<string> Log { get; } = log ?? [];

    public Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);

    public Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        Log.Add("identity.save");
        Identity = identity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        Log.Add("identity.delete");
        Identity = null;
        return Task.CompletedTask;
    }
}

/// <summary>Serializes like the configuration store, so tests see exactly what would persist.</summary>
internal sealed class InMemoryStateStore : IPushRelayStateStore
{
    private string? _json;
    private bool _optIn;

    public int SaveCount { get; private set; }

    public PushRelayState Load()
    {
        var state = _json is null ? new PushRelayState() : JsonSerializer.Deserialize(_json, PushRelayJsonContext.Default.PushRelayState) ?? new PushRelayState();
        state.UserOptIn = _optIn;
        return state;
    }

    public void Save(PushRelayState state)
    {
        SaveCount++;
        _optIn = state.UserOptIn;
        _json = JsonSerializer.Serialize(state, PushRelayJsonContext.Default.PushRelayState);
    }

    public void Clear() => _json = null;
}

internal sealed class FakePushTokenProvider : IPushTokenProvider
{
    public bool IsPushChannelAvailable { get; set; } = true;
    public string? CurrentToken { get; private set; }
    public string? LastFailure => null;
    public event EventHandler? TokenChanged;

    public void SetToken(string? token)
    {
        CurrentToken = token;
        TokenChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeDeviceInfo(string platform = PushRelayPlatforms.Ios) : IPushRelayDeviceInfo
{
    public string? Name { get; set; } = "Test iPhone";
    public string? OsVersion { get; set; } = "iOS 26.1";
    public PushRelayDeviceDescriptor Describe() => new(platform, Name, OsVersion);
}

internal sealed class SettableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A scriptable relay: records each call and answers from per-operation callbacks.</summary>
internal sealed class FakeRelayClient : IPushRelayClient
{
    public List<string> Calls { get; } = [];
    public List<PushRelayRegisterDeviceRequest> Registrations { get; } = [];
    public List<PushRelayUpdateDeviceRequest> Updates { get; } = [];
    public List<List<string>> AccountSyncs { get; } = [];
    public Dictionary<string, Guid> Subscriptions { get; } = [];

    public Func<PushRelayRegisterDeviceRequest, PushRelayRegisterDeviceResponse>? OnRegister { get; set; }
    public Func<Guid, PushRelayDevice>? OnGetDevice { get; set; }
    public Func<Guid, Exception?>? DeviceCallFailure { get; set; }
    public Func<PushRelayCreateMailboxRequest, PushRelayMailbox>? OnCreate { get; set; }
    public Func<Guid, PushRelayMailbox>? OnUpdateCredentials { get; set; }
    public Func<Exception>? OnDelete { get; set; }
    public Func<Exception?>? OnPushTest { get; set; }
    public TaskCompletionSource? BlockRegister { get; set; }

    public Task<PushRelayWellKnown> GetWellKnownAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PushRelayWellKnown("1", 1, null, null, null, null, null, null));

    public async Task<PushRelayRegisterDeviceResponse> RegisterDeviceAsync(PushRelayRegisterDeviceRequest request, CancellationToken cancellationToken = default)
    {
        Record("register");
        Registrations.Add(request);
        if (BlockRegister is { } block)
            await block.Task.WaitAsync(cancellationToken);
        return OnRegister?.Invoke(request) ?? new PushRelayRegisterDeviceResponse(Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    public Task<PushRelayDevice> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        Record("get");
        ThrowIfDeviceFails(deviceId);
        return Task.FromResult(OnGetDevice?.Invoke(deviceId) ?? Device(deviceId, ServerMailboxes()));
    }

    public Task<PushRelayDevice> UpdateDeviceAsync(Guid deviceId, PushRelayUpdateDeviceRequest request, CancellationToken cancellationToken = default)
    {
        Record("patch");
        Updates.Add(request);
        ThrowIfDeviceFails(deviceId);
        return Task.FromResult(Device(deviceId, ServerMailboxes()));
    }

    public Task DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        Record("delete");
        return OnDelete is { } failure ? Task.FromException(failure()) : Task.CompletedTask;
    }

    public Task<PushRelayPushTestResponse> SendPushTestAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        Record("push-test");
        return OnPushTest?.Invoke() is { } failure
            ? Task.FromException<PushRelayPushTestResponse>(failure)
            : Task.FromResult(new PushRelayPushTestResponse("queued"));
    }

    public Task<PushRelaySyncAccountsResponse> SyncAccountsAsync(Guid deviceId, List<string> clientAccountIds, CancellationToken cancellationToken = default)
    {
        Record("accounts");
        AccountSyncs.Add(clientAccountIds);
        ThrowIfDeviceFails(deviceId);
        foreach (var stale in Subscriptions.Keys.Where(id => !clientAccountIds.Contains(id)).ToList())
            Subscriptions.Remove(stale);
        return Task.FromResult(new PushRelaySyncAccountsResponse(0, ServerMailboxes()));
    }

    public Task<PushRelayMailbox> CreateMailboxAsync(Guid deviceId, PushRelayCreateMailboxRequest request, CancellationToken cancellationToken = default)
    {
        Record("create:" + request.ClientAccountId);
        ThrowIfDeviceFails(deviceId);
        try
        {
            var mailbox = OnCreate?.Invoke(request) ?? Mailbox(Guid.NewGuid(), request.ClientAccountId);
            Subscriptions[request.ClientAccountId] = mailbox.Id;
            return Task.FromResult(mailbox);
        }
        catch (Exception ex)
        {
            return Task.FromException<PushRelayMailbox>(ex);
        }
    }

    public Task<PushRelayMailbox> UpdateMailboxCredentialsAsync(Guid deviceId, Guid subscriptionId, PushRelayUpdateCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        Record("put:" + subscriptionId);
        try
        {
            var accountId = Subscriptions.FirstOrDefault(x => x.Value == subscriptionId).Key ?? string.Empty;
            return Task.FromResult(OnUpdateCredentials?.Invoke(subscriptionId) ?? Mailbox(subscriptionId, accountId));
        }
        catch (Exception ex)
        {
            return Task.FromException<PushRelayMailbox>(ex);
        }
    }

    public Task DeleteMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PushRelayResyncResponse> ResyncMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default)
        => Task.FromResult(new PushRelayResyncResponse("queued", null));

    public Task<PushRelayMailboxList> ListMailboxesAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(new PushRelayMailboxList(ServerMailboxes()));

    public static PushRelayMailbox Mailbox(Guid id, string clientAccountId, string status = PushRelayMailboxStatuses.Active, bool muted = false)
        => new(id, Guid.NewGuid(), "imap", "user@example.test", clientAccountId, status, null, null, null, null, null, null, DateTimeOffset.UtcNow, null, muted);

    public static PushRelayApiException Error(int status, string code, string? reason = null, Guid? subscriptionId = null, int? retryAfter = null)
        => new(new PushRelayProblem(code, status, Reason: reason, SubscriptionId: subscriptionId, RetryAfterSeconds: retryAfter));

    private List<PushRelayMailbox> ServerMailboxes() => Subscriptions.Select(x => Mailbox(x.Value, x.Key)).ToList();

    private static PushRelayDevice Device(Guid deviceId, List<PushRelayMailbox> mailboxes)
        => new(deviceId, "ios", true, null, null, "1.0", "1", DateTimeOffset.UtcNow, mailboxes, null, null);

    private void ThrowIfDeviceFails(Guid deviceId)
    {
        if (DeviceCallFailure?.Invoke(deviceId) is { } failure)
            throw failure;
    }

    private void Record(string call)
    {
        lock (Calls) Calls.Add(call);
    }
}
