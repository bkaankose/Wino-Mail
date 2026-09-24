using System.Net;
using Wino.Mapi.Rops;
using Wino.Mapi.Transport;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>
/// A connected, logged-on MAPI/HTTP session: the transport plus the logon handle and the special
/// folder ids the logon returned. Everything above the ROP level starts from one of these.
///
/// Each ROP goes in its own Execute for now. Batching is what Outlook does and is faster, but when
/// a handle or a column is wrong a batch reports it as one opaque failure, and everything about
/// this protocol so far has argued for making failures specific. Batching is a later optimisation.
/// </summary>
public sealed class MapiSession : IAsyncDisposable
{
    private readonly MapiHttpTransport _transport;
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
    /// Sends one Execute and unwraps it. The returned handle table is the server's view: slots the
    /// ROPs filled carry new handles, the rest echo what was sent.
    /// </summary>
    public async Task<(byte[] Rops, uint[] Handles)> ExecuteAsync(byte[] ropBytes, IReadOnlyList<uint> handleTable, CancellationToken cancellationToken = default, bool interactive = false)
    {
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
