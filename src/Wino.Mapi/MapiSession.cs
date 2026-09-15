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
    public async Task<(byte[] Rops, uint[] Handles)> ExecuteAsync(byte[] ropBytes, IReadOnlyList<uint> handleTable, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _transport.ExecuteAsync(ropBytes, handleTable, cancellationToken).ConfigureAwait(false);
            response.EnsureTransportSucceeded();
            return RopExecute.ParseExecuteResponse(response.Body);
        }
        catch (MapiTransportException ex) when (ex.IsContextNotFound || ex.HttpStatus == HttpStatusCode.Unauthorized)
        {
            Faulted = true;
            throw;
        }
        catch (HttpRequestException)
        {
            Faulted = true;
            throw;
        }
    }

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

    /// <summary>Releases a server object handle. Best effort: a failure here is not worth surfacing.</summary>
    public async Task ReleaseAsync(uint handle, CancellationToken cancellationToken = default)
    {
        try
        {
            await _transport.ExecuteAsync(RopMessage.BuildRelease(0), [handle], cancellationToken).ConfigureAwait(false);
        }
        catch (MapiException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected)
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
