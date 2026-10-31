using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Mapi.Ics;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>
/// The client's ICS state for one folder: the four blobs the server hands back after a sync and
/// wants uploaded before the next. Opaque to the client; serialized as JSON for storage.
/// </summary>
public sealed class IcsState
{
    public byte[]? IdsetGiven { get; set; }
    public byte[]? CnsetSeen { get; set; }
    public byte[]? CnsetSeenFAI { get; set; }
    public byte[]? CnsetRead { get; set; }

    public bool IsEmpty => IdsetGiven is null && CnsetSeen is null && CnsetSeenFAI is null && CnsetRead is null;

    public string Serialize() => JsonSerializer.Serialize(this, IcsStateJsonContext.Default.IcsState);

    public static IcsState? Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('{'))
            return null;

        try
        {
            var state = JsonSerializer.Deserialize(text, IcsStateJsonContext.Default.IcsState);
            if (state is null)
                return null;

            // The serializer turns a null blob into an empty one; an empty state blob means the same thing.
            state.IdsetGiven = state.IdsetGiven is { Length: > 0 } ? state.IdsetGiven : null;
            state.CnsetSeen = state.CnsetSeen is { Length: > 0 } ? state.CnsetSeen : null;
            state.CnsetSeenFAI = state.CnsetSeenFAI is { Length: > 0 } ? state.CnsetSeenFAI : null;
            state.CnsetRead = state.CnsetRead is { Length: > 0 } ? state.CnsetRead : null;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(IcsState))]
internal partial class IcsStateJsonContext : JsonSerializerContext;

/// <summary>What one contents sync produced.</summary>
public sealed class IcsContentsResult
{
    /// <summary>New or changed messages, with the requested properties.</summary>
    public List<MapiMessageInfo> Changes { get; } = [];

    /// <summary>Message ids deleted (or moved out) since the uploaded state.</summary>
    public List<ulong> DeletedMessageIds { get; } = [];

    /// <summary>Message ids whose read flag changed, and the new value.</summary>
    public List<(ulong MessageId, bool IsRead)> ReadStateChanges { get; } = [];

    public IcsState NewState { get; } = new();

    public int Buffers { get; set; }
    public long Bytes { get; set; }
    public bool IsInitial { get; set; }
}

/// <summary>
/// Incremental contents synchronization: the MS-OXCFXICS download path over a session.
/// The first sync of a folder (no state) yields every message; later ones only what changed, plus
/// deletions and read-state changes, and the server never has to be asked "what do you have" again.
/// </summary>
public static class MapiIcsOperations
{
    // Handle table: 0 logon, 1 folder, 2 sync context, 3 transfer-state source.
    private const int Slots = 4;

    /// <summary>The buffer size asked for per GetBuffer; well inside the 32KB response cap with its headers.</summary>
    private const ushort BufferSize = 24 * 1024;

    public static async Task<IcsContentsResult> SyncContentsAsync(MapiSession session, ulong folderId, IcsState? state, IReadOnlyList<uint> propertyTags,
        CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            // Normal messages only (no FAI), with read-state changes, restricted to the properties asked for.
            // Unicode is required with OnlySpecifiedProperties for string values to come back as PtypString.
            var flags = RopSync.SynchronizationFlags.Unicode | RopSync.SynchronizationFlags.Normal
                      | RopSync.SynchronizationFlags.ReadState | RopSync.SynchronizationFlags.OnlySpecifiedProperties
                      | RopSync.SynchronizationFlags.NoForeignIdentifiers;
            var extra = RopSync.SynchronizationExtraFlags.Eid | RopSync.SynchronizationExtraFlags.MessageSize | RopSync.SynchronizationExtraFlags.OrderByDeliveryTime;

            (rops, returned) = await session.ExecuteAsync(
                RopSync.BuildSyncConfigure(1, 2, RopSync.SynchronizationType.Contents, RopSync.SendOptions.Unicode, flags, extra, propertyTags),
                handles, cancellationToken).ConfigureAwait(false);
            RopSync.ParseSyncConfigure(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var result = new IcsContentsResult { IsInitial = state is null || state.IsEmpty };

                if (!result.IsInitial)
                    await UploadStateAsync(session, handles, state!, cancellationToken).ConfigureAwait(false);

                await DrainAsync(session, handles, 2, result, propertyTags, cancellationToken, diagnostics).ConfigureAwait(false);

                // The new state: a second FastTransfer source, whose stream is just the four properties.
                (rops, returned) = await session.ExecuteAsync(RopSync.BuildSyncGetTransferState(2, 3), handles, cancellationToken).ConfigureAwait(false);
                RopSync.ParseSyncGetTransferState(new RopReader(rops));
                handles = MapiSession.MergeHandles(handles, returned, Slots);

                try
                {
                    var stateResult = new IcsContentsResult();
                    await DrainAsync(session, handles, 3, stateResult, propertyTags, cancellationToken, null).ConfigureAwait(false);
                    CopyState(stateResult.NewState, result.NewState);
                }
                finally
                {
                    await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
                }

                diagnostics?.Invoke($"ics 0x{folderId:X16}: {(result.IsInitial ? "initial" : "incremental")}, {result.Changes.Count} changes, {result.DeletedMessageIds.Count} deletions, {result.ReadStateChanges.Count} read changes, {result.Buffers} buffers / {result.Bytes} bytes, state {(result.NewState.IsEmpty ? "EMPTY" : "ok")}");
                return result;
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Long-term ids to this logon's message ids, batched 50 per Execute. Ids the server will not
    /// convert are left out; the mapping is by position so a short batch response is still safe.
    /// </summary>
    public static async Task<Dictionary<byte[], ulong>> ResolveMessageIdsAsync(MapiSession session, IReadOnlyList<byte[]> longTermIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<byte[], ulong>(ReferenceEqualityComparer.Instance);
        uint[] handles = [session.LogonHandle];

        foreach (var batch in longTermIds.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rops, _) = await session.ExecuteAsync(RopIds.BuildIdFromLongTermIdBatch(0, batch), handles, cancellationToken).ConfigureAwait(false);

            List<ulong> ids;
            try
            {
                ids = RopIds.ParseIdFromLongTermIdBatch(rops, batch.Length);
            }
            catch (MapiRopException)
            {
                ids = [];
            }

            for (var i = 0; i < ids.Count && i < batch.Length; i++)
                result[batch[i]] = ids[i];
        }

        return result;
    }

    private static async Task UploadStateAsync(MapiSession session, uint[] handles, IcsState state, CancellationToken cancellationToken)
    {
        foreach (var (tag, blob) in new[]
        {
            (FxTags.MetaTagIdsetGiven, state.IdsetGiven),
            (FxTags.MetaTagCnsetSeen, state.CnsetSeen),
            (FxTags.MetaTagCnsetSeenFAI, state.CnsetSeenFAI),
            (FxTags.MetaTagCnsetRead, state.CnsetRead),
        })
        {
            if (blob is null)
                continue;

            var (rops, _) = await session.ExecuteAsync(RopSync.BuildUploadStateStreamBegin(2, tag, (uint)blob.Length), handles, cancellationToken).ConfigureAwait(false);
            RopSync.ParseUploadStateStream(new RopReader(rops), RopSync.RopSyncUploadStateStreamBegin);

            foreach (var chunk in blob.Chunk(16 * 1024))
            {
                (rops, _) = await session.ExecuteAsync(RopSync.BuildUploadStateStreamContinue(2, chunk), handles, cancellationToken).ConfigureAwait(false);
                RopSync.ParseUploadStateStream(new RopReader(rops), RopSync.RopSyncUploadStateStreamContinue);
            }

            (rops, _) = await session.ExecuteAsync(RopSync.BuildUploadStateStreamEnd(2), handles, cancellationToken).ConfigureAwait(false);
            RopSync.ParseUploadStateStream(new RopReader(rops), RopSync.RopSyncUploadStateStreamEnd);
        }
    }

    /// <summary>Pulls buffers until Done, feeding the element reader and folding elements into the result.</summary>
    private static async Task DrainAsync(MapiSession session, uint[] handles, byte sourceHandleIndex, IcsContentsResult result, IReadOnlyList<uint> propertyTags,
        CancellationToken cancellationToken, Action<string>? diagnostics)
    {
        var reader = new FastTransferReader();
        var folder = new ElementFolder(result, propertyTags);
        var guard = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++guard > 100_000)
                throw new MapiFormatException("FastTransfer stream did not finish after 100000 buffers.");

            var (rops, _) = await session.ExecuteAsync(RopSync.BuildFastTransferSourceGetBuffer(sourceHandleIndex, BufferSize), handles, cancellationToken).ConfigureAwait(false);
            var buffer = RopSync.ParseFastTransferSourceGetBuffer(new RopReader(rops));

            if (buffer.BackoffMilliseconds is { } backoff)
            {
                diagnostics?.Invoke($"ics: server busy, backing off {backoff} ms");
                await Task.Delay((int)Math.Min(backoff, 30_000), cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.Buffers++;
            result.Bytes += buffer.Data.Length;

            if (result.Buffers == 1 && diagnostics is not null && buffer.Data.Length > 0)
                diagnostics("ics first buffer head:" + Environment.NewLine + HexDump.Render(buffer.Data, 160));

            foreach (var element in reader.Feed(buffer.Data))
                folder.Fold(element);

            if (buffer.Status == RopSync.TransferStatus.Done)
                break;

            if (buffer.Status == RopSync.TransferStatus.Error)
                throw new MapiFormatException("FastTransfer source reported TransferStatus Error.");
        }

        if (reader.PendingBytes > 0)
            throw new MapiFormatException($"FastTransfer stream ended with {reader.PendingBytes} unparsed bytes; the element encoding drifted.");

        folder.Finish();
    }

    private static void CopyState(IcsState from, IcsState to)
    {
        to.IdsetGiven = from.IdsetGiven;
        to.CnsetSeen = from.CnsetSeen;
        to.CnsetSeenFAI = from.CnsetSeenFAI;
        to.CnsetRead = from.CnsetRead;
    }

    /// <summary>
    /// Folds the element sequence into the result. A contents sync is, in order:
    ///   [IncrSyncChg header IncrSyncMessage props (children)]*  [IncrSyncDel props]  [IncrSyncRead props]
    ///   IncrSyncStateBegin props IncrSyncStateEnd  IncrSyncEnd
    /// Message children (recipients, attachments, embedded messages) may appear even when not asked
    /// for; they are skipped by depth, never parsed into the message.
    /// </summary>
    private sealed class ElementFolder(IcsContentsResult result, IReadOnlyList<uint> propertyTags)
    {
        private enum Section { None, ChangeHeader, Message, Deletions, ReadState, State }

        private Section _section;
        private int _childDepth;
        private ulong _currentMid;
        private readonly Dictionary<uint, PropertyValue> _current = new();

        public void Fold(FxElement element)
        {
            if (element.IsMarker)
            {
                switch (element.Tag)
                {
                    case FxTags.IncrSyncChg:
                    case FxTags.IncrSyncChgPartial:
                        FlushMessage();
                        _section = Section.ChangeHeader;
                        _childDepth = 0;
                        break;
                    case FxTags.IncrSyncMessage:
                        _section = Section.Message;
                        break;
                    case FxTags.StartRecip:
                    case FxTags.NewAttach:
                    case FxTags.StartEmbed:
                        _childDepth++;
                        break;
                    case FxTags.EndToRecip:
                    case FxTags.EndAttach:
                    case FxTags.EndEmbed:
                        _childDepth = Math.Max(0, _childDepth - 1);
                        break;
                    case FxTags.IncrSyncDel:
                        FlushMessage();
                        _section = Section.Deletions;
                        break;
                    case FxTags.IncrSyncRead:
                        FlushMessage();
                        _section = Section.ReadState;
                        break;
                    case FxTags.IncrSyncStateBegin:
                        FlushMessage();
                        _section = Section.State;
                        break;
                    case FxTags.IncrSyncStateEnd:
                    case FxTags.IncrSyncEnd:
                        FlushMessage();
                        _section = Section.None;
                        break;
                    case FxTags.FXErrorInfo:
                        throw new MapiFormatException("FastTransfer stream carries FXErrorInfo: the server reported an error mid-stream.");
                }

                return;
            }

            if (_childDepth > 0)
                return;

            switch (_section)
            {
                case Section.ChangeHeader:
                    if (element.Tag == PropertyTags.Mid && element.Value is ulong mid)
                        _currentMid = mid;
                    _current[element.Tag] = PropertyValue.Of(element.Tag, element.Value);
                    break;

                case Section.Message:
                    _current[element.Tag] = PropertyValue.Of(element.Tag, element.Value);
                    break;

                case Section.Deletions:
                    if (element.Tag is FxTags.MetaTagIdsetDeleted or FxTags.MetaTagIdsetNoLongerInScope or FxTags.MetaTagIdsetExpired && element.Value is byte[] deleted)
                        result.DeletedMessageIds.AddRange(IdSet.DecodeIds(deleted));
                    break;

                case Section.ReadState:
                    if (element.Tag == FxTags.MetaTagIdsetRead && element.Value is byte[] read)
                        result.ReadStateChanges.AddRange(IdSet.DecodeIds(read).Select(id => (id, true)));
                    else if (element.Tag == FxTags.MetaTagIdsetUnread && element.Value is byte[] unread)
                        result.ReadStateChanges.AddRange(IdSet.DecodeIds(unread).Select(id => (id, false)));
                    break;

                case Section.State:
                    switch (element.Tag)
                    {
                        case FxTags.MetaTagIdsetGiven: result.NewState.IdsetGiven = element.Value as byte[]; break;
                        case FxTags.MetaTagCnsetSeen: result.NewState.CnsetSeen = element.Value as byte[]; break;
                        case FxTags.MetaTagCnsetSeenFAI: result.NewState.CnsetSeenFAI = element.Value as byte[]; break;
                        case FxTags.MetaTagCnsetRead: result.NewState.CnsetRead = element.Value as byte[]; break;
                    }

                    break;
            }
        }

        public void Finish() => FlushMessage();

        private void FlushMessage()
        {
            if (_currentMid == 0 && _current.Count == 0)
                return;

            if (_currentMid != 0)
            {
                // Cells in the requested column order, absent where the stream had nothing.
                var cells = new PropertyValue[propertyTags.Count];
                for (var i = 0; i < propertyTags.Count; i++)
                    cells[i] = _current.TryGetValue(propertyTags[i], out var value) ? value : PropertyValue.Absent(propertyTags[i]);

                result.Changes.Add(new MapiMessageInfo(_currentMid, cells));
            }

            _currentMid = 0;
            _current.Clear();
        }
    }
}
