using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Transport;

/// <summary>
/// MAPI/HTTP transport (MS-OXCMAPIHTTP). Requests are POSTed to the mailbox's emsmdb endpoint with
/// the operation in an X-RequestType header and a packed little-endian body; the session is carried
/// in cookies the server hands back on Connect.
///
/// The endpoint is not the bare /mapi/emsmdb path: the routable URL carries a MailboxId, which the
/// front end uses to reach the mailbox's own server. Autodiscover provides it
/// (<see cref="MapiAutodiscover"/>).
/// </summary>
public sealed class MapiHttpTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Action<string>? _trace;
    private readonly TimeSpan _defaultTimeout;

    /// <summary>The least silence that counts as the server having stopped talking.</summary>
    private static readonly TimeSpan MinimumIdleWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a request somebody is waiting on may go completely unanswered before it is given up.
    ///
    /// Shorter than the default because of who is waiting, not because of what is being asked. It
    /// governs the server STARTING to reply: once it is talking, keep-alives keep the request alive
    /// however long the work takes, since cutting off a server that is working is what poisons the
    /// session. So this is the "nothing at all is happening" case, and fifteen seconds of that in
    /// front of somebody saving a draft is already a long time.
    /// </summary>
    internal static readonly TimeSpan InteractiveReplyTimeout = TimeSpan.FromSeconds(15);

    // X-ClientInfo identifies the client INSTANCE and must not change within a session; X-RequestId
    // is that instance plus a sequence number. Minting a fresh guid per request makes the server
    // treat every call as a different client.
    private readonly Guid _clientInstance = Guid.NewGuid();
    private int _requestSequence;

    // Connect hands back the session cookies. They are echoed explicitly rather than left to the
    // CookieContainer, because the session is the whole game here and losing it surfaces as
    // ContextNotFound on the next Execute rather than as anything cookie-shaped.
    private readonly List<string> _sessionCookies = [];

    // Bearer credentials only: the current token and how to replace it after a 401.
    private string? _bearerToken;
    private readonly Func<CancellationToken, Task<string?>>? _refreshBearer;

    /// <param name="trace">
    /// Optional per-request trace line (method, request type, sizes, status). Never carries a body,
    /// a token or a cookie value, so it is safe to route to the ordinary log.
    /// </param>
    public MapiHttpTransport(Uri endpoint, MapiCredential credential, string userAgent, Action<string>? trace = null, TimeSpan? timeout = null)
    {
        _endpoint = endpoint;
        _trace = trace;

        // A cookie container is not optional: Connect returns MapiContext/MapiSequence cookies that
        // every subsequent Execute must echo, or the server has no idea which session is speaking.
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        MapiTls.Apply(handler);

        if (credential is MapiCredential.Integrated integrated)
        {
            var credentials = new CredentialCache
            {
                { endpoint, integrated.Scheme, integrated.Credentials ?? CredentialCache.DefaultNetworkCredentials },
            };

            handler.Credentials = credentials;
            handler.PreAuthenticate = true;
        }

        // HttpClient.Timeout can only be shortened per request, never extended, and NotificationWait
        // legitimately hangs for five minutes. So the client itself has no timeout and every request gets
        // its own: the default below, or the override a caller passes.
        _defaultTimeout = timeout ?? TimeSpan.FromSeconds(60);
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        if (credential is MapiCredential.Bearer bearer)
        {
            _bearerToken = bearer.Token;
            _refreshBearer = bearer.Refresh;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token);
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public Uri Endpoint => _endpoint;

    /// <summary>
    /// Connect request (MS-OXCMAPIHTTP 2.2.4.1). Establishes the session for a mailbox identified by
    /// its legacyExchangeDN.
    /// </summary>
    public Task<MapiResponse> ConnectAsync(string userDn, CancellationToken cancellationToken = default)
    {
        var body = new RopWriter();
        body.AsciiZ(userDn);          // UserDn
        body.UInt32(0);               // Flags
        body.UInt32(1252);            // DefaultCodePage
        body.UInt32(1033);            // LcidSort
        body.UInt32(1033);            // LcidString
        body.UInt32(0);               // AuxiliaryBufferSize

        return SendAsync("Connect", body.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Execute request: carries one or more ROPs and the server object handle table.
    /// <paramref name="interactive"/> marks a request somebody is waiting on, which is given less
    /// time to go unanswered - see <see cref="InteractiveReplyTimeout"/>.
    /// </summary>
    public Task<MapiResponse> ExecuteAsync(byte[] ropBytes, IReadOnlyList<uint> handleTable, CancellationToken cancellationToken = default, bool interactive = false)
        => SendAsync("Execute", RopExecute.BuildExecuteBody(ropBytes, handleTable), cancellationToken, interactive ? InteractiveReplyTimeout : null);

    /// <summary>
    /// NotificationWait request (MS-OXCMAPIHTTP 2.2.4.4): a long poll that returns when the server has a
    /// notification pending for the session, or after its own timeout (up to five minutes) with
    /// EventPending false. The caller then sends an Execute to collect the RopNotify responses.
    /// </summary>
    public async Task<bool> NotificationWaitAsync(CancellationToken cancellationToken = default)
    {
        var body = new RopWriter();
        body.UInt32(0);               // Flags
        body.UInt32(0);               // AuxiliaryBufferSize

        // The default client timeout is a minute; this request legitimately hangs for five.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var response = await SendAsync("NotificationWait", body.ToArray(), linked.Token, TimeSpan.FromMinutes(7)).ConfigureAwait(false);
        response.EnsureTransportSucceeded();

        // Response: StatusCode, ErrorCode, EventPending (uint32 boolean), AuxiliaryBufferSize, AuxiliaryBuffer.
        var reader = new RopReader(response.Body);
        var status = reader.UInt32();
        var error = reader.UInt32();
        if (status != 0 || error != 0)
            throw new MapiFormatException($"NotificationWait returned StatusCode 0x{status:X8}, ErrorCode 0x{error:X8}.");

        return reader.UInt32() != 0;
    }

    /// <summary>
    /// An address book request (MS-OXCMAPIHTTP 2.2.5: Bind, GetMatches, Unbind, ...) on a transport
    /// whose endpoint is the AddressBook URL. Same framing and session cookies as the mailbox side.
    /// </summary>
    public Task<MapiResponse> SendAddressBookAsync(string requestType, byte[] body, CancellationToken cancellationToken = default)
        => SendAsync(requestType, body, cancellationToken);

    /// <summary>Disconnect request (MS-OXCMAPIHTTP 2.2.4.3). Politely drops the session.</summary>
    public Task<MapiResponse> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var body = new RopWriter();
        body.UInt32(0);               // AuxiliaryBufferSize

        return SendAsync("Disconnect", body.ToArray(), cancellationToken);
    }

    private async Task<MapiResponse> SendAsync(string requestType, byte[] body, CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        var response = await SendOnceAsync(requestType, body, cancellationToken, requestTimeout).ConfigureAwait(false);
        if (response.HttpStatus != HttpStatusCode.Unauthorized || _refreshBearer is null)
            return response;

        // The bearer token expired under a live session (they last about an hour; a kept session can
        // outlive that). Mint a replacement and repeat the request once. The same token again means
        // the server rejected one the issuer still considers current, and there is nothing to retry.
        var fresh = await _refreshBearer(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(fresh) || fresh == _bearerToken)
            return response;

        _bearerToken = fresh;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        _trace?.Invoke($"{requestType}: 401, bearer token refreshed; retrying once");
        return await SendOnceAsync(requestType, body, cancellationToken, requestTimeout).ConfigureAwait(false);
    }

    private async Task<MapiResponse> SendOnceAsync(string requestType, byte[] body, CancellationToken cancellationToken, TimeSpan? requestTimeout)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http");

        var sequence = Interlocked.Increment(ref _requestSequence);
        request.Headers.TryAddWithoutValidation("X-RequestType", requestType);
        request.Headers.TryAddWithoutValidation("X-RequestId", $"{{{_clientInstance}}}:{sequence}");
        request.Headers.TryAddWithoutValidation("X-ClientInfo", $"{{{_clientInstance}}}");
        request.Headers.TryAddWithoutValidation("X-ClientApplication", "Outlook/15.00.0000.0000");
        request.Headers.TryAddWithoutValidation("Accept", "application/mapi-http");

        lock (_sessionCookies)
        {
            if (_sessionCookies.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _sessionCookies));
            }
        }

        _trace?.Invoke($"--> {requestType} {body.Length} byte body");

        // The reply timeout covers getting an answer STARTED, and nothing after that: its tokens are
        // scoped to this block so the body read below cannot accidentally be put under them. Doing
        // that would reinstate a flat deadline on a server that is working and talking, which is the
        // whole thing this went to some trouble to stop doing.
        HttpResponseMessage response;
        using (var replyTimeout = new CancellationTokenSource(requestTimeout ?? _defaultTimeout))
        using (var startingToReply = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, replyTimeout.Token))
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, startingToReply.Token).ConfigureAwait(false);
        }

        using var _ = response;

        CaptureSessionCookies(response);

        var pendingPeriod = Header(response, "X-PendingPeriod");
        var idleWindow = IdleWindow(pendingPeriod, SilenceBudget(requestTimeout, _defaultTimeout));
        var payload = await ReadBodyAsync(response, idleWindow, response.Content.Headers.ContentLength, cancellationToken).ConfigureAwait(false);

        var result = new MapiResponse
        {
            HttpStatus = response.StatusCode,
            RequestType = requestType,
            ResponseCodeHeader = Header(response, "X-ResponseCode"),
            PendingPeriod = pendingPeriod,
            ExpirationTime = Header(response, "X-ExpirationTime"),
            ServerId = Header(response, "X-ServerApplication"),
            WwwAuthenticate = Header(response, "WWW-Authenticate"),
            RawBody = payload,
        };

        // The body is a sequence of chunked meta-tags (PROCESSING / PENDING) terminated by DONE,
        // and only then the actual response buffer. Strip the meta-tags before anything tries to
        // parse the payload as a MAPI structure.
        result.Body = StripMetaTags(payload, out var metaTags);
        result.MetaTags = metaTags;

        _trace?.Invoke($"<-- {(int)response.StatusCode} {response.StatusCode}  X-ResponseCode={result.ResponseCodeHeader ?? "(none)"}  meta=[{string.Join(",", metaTags)}]  payload={result.Body.Length} bytes");

        return result;
    }

    /// <summary>
    /// How long the server may go silent before the reply is treated as abandoned.
    ///
    /// MS-OXCMAPIHTTP is explicit that a slow operation is not a dead one: while the server works it
    /// writes PENDING keep-alives into the response body, and X-PendingPeriod says how often to
    /// expect them. So the right question is not "has a minute passed" but "has the server stopped
    /// talking" - three missed keep-alives, with a floor for servers that name a very short period.
    /// </summary>
    internal static TimeSpan IdleWindow(string? pendingPeriod, TimeSpan fallback)
    {
        if (int.TryParse(pendingPeriod, out var milliseconds) && milliseconds > 0)
            return Longer(TimeSpan.FromMilliseconds(milliseconds * 3d), MinimumIdleWindow);

        // No promise made, so fall back to the caller's own patience.
        return fallback;
    }

    /// <summary>
    /// How long silence is tolerated when the server names no keep-alive period.
    ///
    /// Deliberately not the reply timeout. A caller asking to give up SOONER on a server that says
    /// nothing - somebody waiting on a draft save - is not asking to cut off one that is talking,
    /// and treating those as one number would abandon working servers more often, not less. Only a
    /// caller asking for more patience than usual, which is NotificationWait, raises it.
    /// </summary>
    internal static TimeSpan SilenceBudget(TimeSpan? requestTimeout, TimeSpan standard)
        => requestTimeout is { } asked ? Longer(asked, standard) : standard;

    private static TimeSpan Longer(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Reads the response body, allowing the server as long as it likes provided it keeps saying so.
    /// The timeout is reset by every chunk that arrives, so a save that genuinely takes two minutes
    /// completes instead of being abandoned at some fixed mark - and abandoning it was never free:
    /// the request carries on running on the server, and the next one sent down that session is
    /// refused as an invalid sequence, along with everything after it.
    /// </summary>
    internal static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, TimeSpan idleWindow, long? expectedLength = null, CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Sized from Content-Length where the server gave one, and otherwise large enough that an
        // ordinary ROP response needs no doubling at all. The doublings are what the old
        // ReadAsByteArrayAsync avoided by buffering through a pool, and on a phone the ones past
        // 85 KB land on the large-object heap.
        using var collected = new MemoryStream(expectedLength is > 0 and < int.MaxValue ? (int)expectedLength : 64 * 1024);

        // One pair of token sources for the whole read, re-armed before each chunk. CancelAfter on a
        // live source reschedules its timer, which is the same "reset on every chunk" behaviour a
        // fresh source per iteration gave - without allocating a source, a timer and two token
        // registrations for every 16 KB that arrives.
        using var silence = new CancellationTokenSource(idleWindow);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, silence.Token);

        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                silence.CancelAfter(idleWindow);

                var read = await stream.ReadAsync(chunk.AsMemory(), linked.Token).ConfigureAwait(false);

                if (read == 0)
                    break;

                collected.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return collected.ToArray();
    }

    /// <summary>Remembers the pairs Connect sets, so later requests can present them.</summary>
    private void CaptureSessionCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return;
        }

        lock (_sessionCookies)
        {
            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';', 2)[0].Trim();
                var name = pair.Split('=', 2)[0];

                _sessionCookies.RemoveAll(e => e.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                _sessionCookies.Add(pair);
            }
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    /// <summary>
    /// MAPI/HTTP frames its response body as ASCII meta-tags ("PROCESSING", "PENDING", "DONE") each
    /// followed by CRLF, before the real buffer. Returns everything after the DONE marker.
    /// </summary>
    public static byte[] StripMetaTags(byte[] payload, out List<string> metaTags)
    {
        metaTags = [];

        var offset = 0;
        while (true)
        {
            var lineEnd = IndexOfCrLf(payload, offset);
            if (lineEnd < 0)
            {
                // No further CRLF: nothing meta-tag shaped left to strip.
                return payload[offset..];
            }

            var line = Encoding.ASCII.GetString(payload, offset, lineEnd - offset);
            if (line is not ("PROCESSING" or "PENDING" or "DONE"))
            {
                return payload[offset..];
            }

            metaTags.Add(line);
            offset = lineEnd + 2;

            if (line == "DONE")
            {
                // DONE is followed by additional headers (X-ElapsedTime, X-StartTime and friends),
                // each CRLF-terminated, then a blank line, and only then the response buffer.
                // Skipping only the blank line leaves those headers in the payload, where they
                // decode as plausible-looking garbage: an ErrorCode of 0x54747261 is ASCII "artT",
                // the middle of "X-StartTime".
                while (true)
                {
                    var headerEnd = IndexOfCrLf(payload, offset);
                    if (headerEnd < 0)
                    {
                        return payload[offset..];
                    }

                    if (headerEnd == offset)
                    {
                        return payload[(offset + 2)..];   // blank line: the payload starts here
                    }

                    offset = headerEnd + 2;
                }
            }
        }
    }

    private static int IndexOfCrLf(byte[] payload, int from)
    {
        for (var i = from; i + 1 < payload.Length; i++)
        {
            if (payload[i] == (byte)'\r' && payload[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class MapiResponse
{
    public required HttpStatusCode HttpStatus { get; init; }
    public required string RequestType { get; init; }
    public string? ResponseCodeHeader { get; init; }
    public string? PendingPeriod { get; init; }
    public string? ExpirationTime { get; init; }
    public string? ServerId { get; init; }
    public string? WwwAuthenticate { get; init; }
    public required byte[] RawBody { get; init; }
    public byte[] Body { get; set; } = [];
    public List<string> MetaTags { get; set; } = [];

    /// <summary>X-ResponseCode 0 means the transport accepted the request; ROP errors live in the body.</summary>
    public bool TransportSucceeded => ResponseCodeHeader == "0";

    /// <summary>Throws <see cref="MapiTransportException"/> unless the transport accepted the request.</summary>
    public void EnsureTransportSucceeded()
    {
        if (TransportSucceeded)
        {
            return;
        }

        var detail = HttpStatus == HttpStatusCode.Unauthorized
            ? "HTTP 401: the credential was rejected. On-premises Exchange does not advertise Bearer in its challenge even where it accepts one, so WWW-Authenticate says nothing about that."
            : $"HTTP {(int)HttpStatus}, X-ResponseCode {ResponseCodeHeader ?? "(none)"}" + ResponseCodeHeader switch
            {
                "10" => " (ContextNotFound: no session; usually a Connect that already failed)",
                "15" => " (InvalidSequence: two requests were in flight at once in one session; the server rejects every later one in that session too)",
                _ => string.Empty,
            };

        throw new MapiTransportException(RequestType, HttpStatus, ResponseCodeHeader, $"{RequestType} was not accepted by the transport: {detail}");
    }
}
