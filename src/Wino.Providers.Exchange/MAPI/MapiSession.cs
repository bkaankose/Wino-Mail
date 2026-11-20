using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Transport;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>
/// A connected, logged-on MAPI/HTTP session: the transport plus the logon handle and the special
/// folder ids the logon returned. Everything above the ROP level starts from one of these.
///
/// Most ROPs go in their own Execute, so a wrong handle or column fails with its own error. Hot
/// paths batch with <see cref="ExecuteBatchAsync"/> and parse the responses in order, so the first
/// failing ROP still reports itself. On a distant server each Execute costs a full round trip.
/// </summary>
public sealed class MapiSession : IAsyncDisposable
{
    /// <summary>Exchange limits the request RopSize to 32KB; this leaves room for the handle table and queued releases.</summary>
    public const int MaxBatchRopBytes = 28 * 1024;

    private const int MaxReleasesPerExecute = 64;

    private readonly MapiHttpTransport _transport;
    private readonly List<uint> _pendingReleases = [];
    private bool _connected;

    private MapiSession(MapiHttpTransport transport, string userDn, string mailboxDn, bool publicStore)
    {
        _transport = transport;
        UserDn = userDn;
        MailboxDn = mailboxDn;
        IsPublicStore = publicStore;
    }

    public string UserDn { get; }

    /// <summary>The mailbox logged on to: the user's own, or another one (an in-place archive) the user may open.</summary>
    public string MailboxDn { get; }

    public bool IsPublicStore { get; }
    public string? DisplayName { get; private set; }
    public LogonResponse? Logon { get; private set; }

    /// <summary>The logon's server object handle; index 0 of every handle table this session builds.</summary>
    public uint LogonHandle { get; private set; } = RopExecute.NullHandle;

    /// <summary>
    /// Connects as <paramref name="userDn"/> and logs on to <paramref name="mailboxDn"/> (the user's own
    /// mailbox when null), or to the public folder store when <paramref name="publicStore"/>. Failures
    /// are specific: transport, connect refusal, RPC, or ROP.
    /// </summary>
    public static async Task<MapiSession> OpenAsync(MapiHttpTransport transport, string userDn, CancellationToken cancellationToken = default, string? mailboxDn = null, bool publicStore = false)
    {
        var session = new MapiSession(transport, userDn, mailboxDn ?? userDn, publicStore);
        await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await session.LogonAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.ConnectAsync(UserDn, cancellationToken).ConfigureAwait(false);
        response.EnsureTransportSucceeded();

        // Connect response (MS-OXCMAPIHTTP 2.2.4.1.2).
        var reader = new RopReader(response.Body);
        var status = reader.UInt32();
        var error = reader.UInt32();
        reader.UInt32();                 // PollsMax
        reader.UInt32();                 // RetryCount
        reader.UInt32();                 // RetryDelay
        reader.AsciiZ();                 // DnPrefix
        DisplayName = reader.UnicodeZ();

        if (status != 0)
        {
            throw new MapiFormatException($"Connect returned StatusCode 0x{status:X8}.");
        }

        if (error != 0)
        {
            // Stop here. Without a session context every later request fails as ContextNotFound,
            // which looks like a cookie or session-handling bug and is not one.
            throw new MapiConnectException(error);
        }

        _connected = true;
    }

    private async Task LogonAsync(CancellationToken cancellationToken)
    {
        var (rops, handles) = await ExecuteAsync(RopLogon.BuildRequest(MailboxDn, publicStore: IsPublicStore), [RopExecute.NullHandle], cancellationToken).ConfigureAwait(false);
        Logon = RopLogon.ParseResponse(rops);
        LogonHandle = handles.Length > 0 ? handles[0] : RopExecute.NullHandle;
    }

    /// <summary>
    /// Long-polls this session's notification channel; true when RopNotify responses are waiting for the next Execute.
    /// </summary>
    public Task<bool> NotificationWaitAsync(CancellationToken cancellationToken = default)
        => _transport.NotificationWaitAsync(cancellationToken);

    /// <summary>
    /// Sends one Execute and unwraps it. The returned handle table is the server's view: slots the
    /// ROPs filled carry new handles, the rest echo what was sent.
    /// </summary>
    public async Task<(byte[] Rops, uint[] Handles)> ExecuteAsync(byte[] ropBytes, IReadOnlyList<uint> handleTable, CancellationToken cancellationToken = default, bool interactive = false)
    {
        (ropBytes, handleTable) = AppendQueuedReleases(ropBytes, handleTable);

        try
        {
            var response = await _transport.ExecuteAsync(ropBytes, handleTable, cancellationToken, interactive).ConfigureAwait(false);
            response.EnsureTransportSucceeded();
            return RopExecute.ParseExecuteResponse(response.Body);
        }
        catch (Exception ex) when (RetiresSession(ex))
        {
            Faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Sends several ROPs in one Execute. The caller parses the responses in order from one reader; a ROP that
    /// takes a handle another ROP of the same batch outputs refers to it by the same handle table index.
    /// </summary>
    public Task<(byte[] Rops, uint[] Handles)> ExecuteBatchAsync(IReadOnlyList<byte[]> rops, IReadOnlyList<uint> handleTable, CancellationToken cancellationToken = default)
    {
        var length = rops.Sum(r => r.Length);
        if (length > MaxBatchRopBytes)
            throw new ArgumentException($"A batch of {length} ROP bytes exceeds the {MaxBatchRopBytes} byte request limit.", nameof(rops));

        var buffer = new byte[length];
        var offset = 0;
        foreach (var rop in rops)
        {
            rop.CopyTo(buffer, offset);
            offset += rop.Length;
        }

        return ExecuteAsync(buffer, handleTable, cancellationToken);
    }

    /// <summary>
    /// Releases a server object handle with the next Execute instead of in a request of its own. RopRelease has no
    /// response, so riding along changes nothing the caller of that Execute parses. A session that faults or closes
    /// first drops the queue: the server frees the handles with the session context.
    /// </summary>
    public void QueueRelease(uint handle)
    {
        if (handle == RopExecute.NullHandle)
            return;

        lock (_pendingReleases)
            _pendingReleases.Add(handle);
    }

    private (byte[] Rops, IReadOnlyList<uint> Handles) AppendQueuedReleases(byte[] ropBytes, IReadOnlyList<uint> handleTable)
    {
        uint[] releases;
        lock (_pendingReleases)
        {
            if (_pendingReleases.Count == 0 || Faulted)
            {
                _pendingReleases.Clear();
                return (ropBytes, handleTable);
            }

            var take = Math.Min(Math.Min(_pendingReleases.Count, MaxReleasesPerExecute), 255 - handleTable.Count);
            if (take <= 0)
                return (ropBytes, handleTable);

            releases = _pendingReleases.GetRange(0, take).ToArray();
            _pendingReleases.RemoveRange(0, take);
        }

        var table = new uint[handleTable.Count + releases.Length];
        for (var i = 0; i < handleTable.Count; i++)
            table[i] = handleTable[i];

        var rops = new byte[ropBytes.Length + (releases.Length * 3)];
        ropBytes.CopyTo(rops, 0);

        for (var i = 0; i < releases.Length; i++)
        {
            var index = handleTable.Count + i;
            table[index] = releases[i];
            RopMessage.BuildRelease((byte)index).CopyTo(rops, ropBytes.Length + (i * 3));
        }

        return (rops, table);
    }

    /// <summary>
    /// Whether a failed Execute leaves this session unusable. The question is asked this way round
    /// on purpose.
    ///
    /// MS-OXCMAPIHTTP allows one request at a time inside a Session Context, so the only evidence
    /// the context is still clean is a response that came back and parsed. Anything else leaves an
    /// open question the client cannot answer - did the server finish? is the framing still in
    /// step? - and a session carrying an open question poisons every request after it, because the
    /// server refuses the whole context once it sees two requests at once.
    ///
    /// Listing what kills a session instead would mean every new failure defaults to REUSE, which is
    /// how a cancelled request came to be handed back to the pool in the first place, and how the
    /// switch to streaming the body nearly added another: an IO failure part way through a response
    /// is not an HttpRequestException.
    ///
    /// So only one thing is known safe: a well-formed response that carried a ROP-level error. The
    /// exchange completed, the framing held, and the session is exactly as usable as before.
    /// </summary>
    private static bool RetiresSession(Exception exception) => exception is not MapiRopException;

    /// <summary>
    /// True once the server has lost this session (context gone, credential expired, or the
    /// connection dropped): a holder that reuses sessions must open a fresh one instead.
    /// </summary>
    public bool Faulted { get; private set; }

    /// <summary>
    /// Merges a returned handle table over the one that was sent, keeping slots the server did not
    /// fill, padded to <paramref name="slots"/>.
    /// </summary>
    public static uint[] MergeHandles(IReadOnlyList<uint> sent, uint[] returned, int slots)
    {
        var merged = new uint[Math.Max(slots, sent.Count)];
        Array.Fill(merged, RopExecute.NullHandle);
        for (var i = 0; i < sent.Count; i++)
        {
            merged[i] = sent[i];
        }

        for (var i = 0; i < returned.Length && i < merged.Length; i++)
        {
            if (returned[i] != RopExecute.NullHandle)
            {
                merged[i] = returned[i];
            }
        }

        return merged;
    }

    /// <summary>
    /// Releases a server object handle. Best effort as far as the caller is concerned - a failure
    /// here is not worth surfacing - but it goes through <see cref="ExecuteAsync"/> rather than
    /// round the side of it, because a release that was cancelled or refused leaves the session in
    /// the same open question any other request would, and swallowing that quietly used to return a
    /// session to the pool looking healthy.
    /// </summary>
    public async Task ReleaseAsync(uint handle, CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteAsync(RopMessage.BuildRelease(0), [handle], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MapiException or HttpRequestException or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A clean session is disconnected; a faulted one is dropped where it stands.
    ///
    /// Saying goodbye costs a request, and a faulted context is one whose last request left an open
    /// question - see <see cref="RetiresSession"/>. If the server is still working on that request,
    /// the Disconnect is queued behind it and gets no answer, because a Session Context takes one
    /// request at a time. Measured: a cancelled body read of a 139 KB message, then a Disconnect
    /// that never came back. The server retires an idle context on its own, so the courtesy buys
    /// nothing and can only add to work the mailbox is already behind on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connected && !Faulted)
        {
            try
            {
                await _transport.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MapiException or HttpRequestException or OperationCanceledException)
            {
            }
        }

        _transport.Dispose();
    }
}
