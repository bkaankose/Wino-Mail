using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>One row of a folder's contents table, with the list columns.</summary>
public sealed record MapiMessageInfo(ulong MessageId, PropertyValue[] Cells)
{
    private PropertyValue Cell(uint tag) => Array.Find(Cells, c => c.Tag == tag);

    public string? MessageClass => Cell(PropertyTags.MessageClass).AsString;
    public string? Subject => Cell(PropertyTags.Subject).AsString ?? Cell(PropertyTags.NormalizedSubject).AsString;
    public string? FromName => Cell(PropertyTags.SentRepresentingName).AsString ?? Cell(PropertyTags.SenderName).AsString;
    public string? FromAddress => Cell(PropertyTags.SentRepresentingSmtpAddress).AsString ?? Cell(PropertyTags.SenderSmtpAddress).AsString;
    public DateTime? SentTime => Cell(PropertyTags.ClientSubmitTime).Value as DateTime?;
    public DateTime? ReceivedTime => Cell(PropertyTags.MessageDeliveryTime).Value as DateTime?;
    public DateTime? LastModified => Cell(PropertyTags.LastModificationTime).Value as DateTime?;
    public uint MessageFlags => Cell(PropertyTags.MessageFlags).AsUInt32 ?? 0;
    public bool IsRead => (MessageFlags & PropertyTags.MessageFlagRead) != 0;
    public bool IsUnsent => (MessageFlags & PropertyTags.MessageFlagUnsent) != 0;
    public bool HasAttachments => Cell(PropertyTags.HasAttachments).AsBoolean ?? (MessageFlags & PropertyTags.MessageFlagHasAttach) != 0;
    public uint Size => Cell(PropertyTags.MessageSize).AsUInt32 ?? 0;
    public uint Importance => Cell(PropertyTags.Importance).AsUInt32 ?? 1;
    public bool IsFlagged => Cell(PropertyTags.FlagStatus).AsUInt32 == 2;
    public byte[]? ConversationId => Cell(PropertyTags.ConversationId).AsBinary;
    public string? InternetMessageId => Cell(PropertyTags.InternetMessageId).AsString;
    public string? InReplyTo => Cell(PropertyTags.InReplyToId).AsString;
    public string? References => Cell(PropertyTags.InternetReferences).AsString;
    public string? DisplayTo => Cell(PropertyTags.DisplayTo).AsString;
    public byte[]? ChangeKey => Cell(PropertyTags.ChangeKey).AsBinary;
}

/// <summary>An attachment's table row plus, once fetched, its bytes.</summary>
public sealed record MapiAttachmentInfo(uint AttachNumber, uint Method, uint Size, string? FileName, string? MimeType, string? ContentId, bool IsHidden)
{
    /// <summary>PidTagAttachMethod: 1 = by value (a file), 5 = embedded message, 6 = OLE, 7 = by web reference.</summary>
    public bool IsFile => Method == 1;
    public bool IsEmbeddedMessage => Method == 5;

    public byte[]? Data { get; set; }
}

/// <summary>A message's readable content: bodies, original headers, attachments.</summary>
public sealed class MapiMessageContent
{
    public string? Html { get; set; }
    public string? PlainText { get; set; }
    public byte[]? RtfCompressed { get; set; }
    public string? TransportHeaders { get; set; }
    public int InternetCodepage { get; set; }
    public List<MapiAttachmentInfo> Attachments { get; } = [];
}

/// <summary>
/// Message-level operations over a session: the folder's list, one message's content, and the
/// mutations, kept in one place because they share the handle choreography.
/// </summary>
public static class MapiMessageOperations
{
    // Handle table layout used throughout: 0 logon, 1 folder, 2 table-or-message, 3 attachment-table-or-stream, 4 attachment, 5 attachment stream.
    private const int Slots = 6;

    /// <summary>
    /// Reads up to <paramref name="limit"/> messages of a folder, newest delivered first. The limit
    /// bounds the initial download the same way the EWS path does; incremental sync is ICS.
    /// </summary>
    public static async Task<List<MapiMessageInfo>> ReadMessageListAsync(MapiSession session, ulong folderId, int limit, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        var replicas = RopFolder.ParseOpenFolder(new RopReader(rops));
        if (replicas.Count > 0) diagnostics?.Invoke($"folder 0x{folderId:X16} is ghosted; replicas: {string.Join(", ", replicas)}");
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.DeferredErrors), handles, cancellationToken).ConfigureAwait(false);
            var rowCount = RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);
            diagnostics?.Invoke($"contents table of 0x{folderId:X16}: RowCount {rowCount}");

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, PropertyTags.MessageListColumns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSortTable(2, [new RopFolder.SortOrder(PropertyTags.MessageDeliveryTime, Descending: true)]), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSortTable(new RopReader(rops));

                var messages = new List<MapiMessageInfo>(Math.Min(limit, 1024));
                while (messages.Count < limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 22 mostly-short columns: 50 rows stays inside the 32KB response buffer with room.
                    var want = (ushort)Math.Min(50, limit - messages.Count);
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, want), handles, cancellationToken).ConfigureAwait(false);
                    List<PropertyValue[]> rows;
                    try
                    {
                        rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.MessageListColumns);
                    }
                    catch (MapiRopException ex)
                    {
                        diagnostics?.Invoke($"RopQueryRows on 0x{folderId:X16} failed ({ex.Message}); response:" + Environment.NewLine + HexDump.Render(rops, 512));
                        throw replicas.Count > 0 ? new MapiGhostedFolderException(replicas, ex) : ex;
                    }
                    if (rows.Count == 0)
                    {
                        break;
                    }

                    foreach (var row in rows)
                    {
                        if (row[0].AsUInt64 is { } mid)
                        {
                            messages.Add(new MapiMessageInfo(mid, row));
                        }
                    }
                }

                diagnostics?.Invoke($"contents table of 0x{folderId:X16}: {messages.Count} rows read (limit {limit})");
                return messages;
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

    /// <summary>The list-row properties of one message, read off the open message rather than its folder's table.</summary>
    public static async Task<MapiMessageInfo> ReadMessageInfoAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, PropertyTags.MessageListColumns), handles, cancellationToken).ConfigureAwait(false);
            var cells = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), PropertyTags.MessageListColumns);
            return new MapiMessageInfo(messageId, cells);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a message's content: HTML and/or plain body, the original transport headers, and every
    /// attachment's bytes. Small properties come inline; anything the server will not return inline
    /// (NotEnoughMemory) is streamed, which is the normal case for bodies.
    /// </summary>
    public static async Task<MapiMessageContent> ReadMessageContentAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 1, 2), handles, cancellationToken).ConfigureAwait(false);
            RopMessage.ParseOpenMessage(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var content = new MapiMessageContent();

                uint[] tags = [PropertyTags.InternetCodepage, PropertyTags.Html, PropertyTags.Body, PropertyTags.TransportMessageHeaders, PropertyTags.RtfCompressed];
                (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(2, tags), handles, cancellationToken).ConfigureAwait(false);
                var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags);

                content.InternetCodepage = (int)(values[0].AsUInt32 ?? 0);
                var encoding = ResolveEncoding(content.InternetCodepage);

                var html = await InlineOrStreamAsync(session, handles, 2, 3, values[1], PropertyTags.Html, cancellationToken).ConfigureAwait(false);
                if (html is { Length: > 0 })
                    content.Html = encoding.GetString(html);

                var body = await InlineOrStreamAsync(session, handles, 2, 3, values[2], PropertyTags.Body, cancellationToken).ConfigureAwait(false);
                if (body is { Length: > 0 })
                    content.PlainText = DecodeUnicodeProperty(body);

                var headers = await InlineOrStreamAsync(session, handles, 2, 3, values[3], PropertyTags.TransportMessageHeaders, cancellationToken).ConfigureAwait(false);
                if (headers is { Length: > 0 })
                    content.TransportHeaders = DecodeUnicodeProperty(headers);

                if (content.Html is null && content.PlainText is null)
                {
                    // RTF-only message (rare; old Outlook). Kept compressed; the caller decides whether to decode.
                    content.RtfCompressed = await InlineOrStreamAsync(session, handles, 2, 3, values[4], PropertyTags.RtfCompressed, cancellationToken).ConfigureAwait(false);
                }

                diagnostics?.Invoke($"message 0x{messageId:X16}: html {content.Html?.Length ?? 0} chars, text {content.PlainText?.Length ?? 0} chars, headers {content.TransportHeaders?.Length ?? 0} chars, codepage {content.InternetCodepage}");

                await ReadAttachmentsAsync(session, handles, content, cancellationToken, diagnostics).ConfigureAwait(false);
                return content;
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

    private static async Task ReadAttachmentsAsync(MapiSession session, uint[] handles, MapiMessageContent content, CancellationToken cancellationToken, Action<string>? diagnostics)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(2, 3), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(3, PropertyTags.AttachmentColumns), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseSetColumns(new RopReader(rops));

            (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(3, 100), handles, cancellationToken).ConfigureAwait(false);
            var rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.AttachmentColumns);

            foreach (var row in rows)
            {
                if (row[0].AsUInt32 is not { } number)
                    continue;

                content.Attachments.Add(new MapiAttachmentInfo(
                    number,
                    row[1].AsUInt32 ?? 0,
                    row[2].AsUInt32 ?? 0,
                    row[3].AsString ?? row[4].AsString ?? row[8].AsString,
                    row[5].AsString,
                    row[6].AsString,
                    row[7].AsBoolean ?? false));
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
        }

        foreach (var attachment in content.Attachments.Where(a => a.IsFile))
        {
            cancellationToken.ThrowIfCancellationRequested();

            (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildOpenAttachment(2, 4, attachment.AttachNumber), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseOpenAttachment(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                uint[] tags = [PropertyTags.AttachDataBinary];
                (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(4, tags), handles, cancellationToken).ConfigureAwait(false);
                var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags);
                attachment.Data = await InlineOrStreamAsync(session, handles, 4, 5, values[0], PropertyTags.AttachDataBinary, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await session.ReleaseAsync(handles[4], cancellationToken).ConfigureAwait(false);
            }
        }

        diagnostics?.Invoke($"attachments: {content.Attachments.Count} rows, {content.Attachments.Count(a => a.Data is not null)} fetched");
    }

    /// <summary>
    /// Takes a property inline when the server returned it, else streams it when the cell says
    /// NotEnoughMemory; any other error cell means the property is absent (null).
    /// </summary>
    private static async Task<byte[]?> InlineOrStreamAsync(MapiSession session, uint[] handles, byte objectHandleIndex, byte streamHandleIndex, PropertyValue cell, uint tag, CancellationToken cancellationToken)
    {
        if (cell.Error is null && cell.Value is not null)
        {
            return cell.Value switch
            {
                byte[] bytes => bytes,
                string text => Encoding.Unicode.GetBytes(text),
                _ => null,
            };
        }

        if (cell.Error != MapiRopException.NotEnoughMemory)
        {
            return null;
        }

        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenStream(tag, objectHandleIndex, streamHandleIndex), handles, cancellationToken).ConfigureAwait(false);
        var size = RopMessage.ParseOpenStream(new RopReader(rops));
        var streamHandles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var buffer = new MemoryStream((int)Math.Min(size, 64 * 1024 * 1024));
            while (buffer.Length < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = Math.Min(RopMessage.SafeReadChunk, size - (uint)buffer.Length);
                (rops, _) = await session.ExecuteAsync(RopMessage.BuildReadStream(streamHandleIndex, want), streamHandles, cancellationToken).ConfigureAwait(false);
                var data = RopMessage.ParseReadStream(new RopReader(rops));
                if (data.Length == 0)
                    break;
                buffer.Write(data);
            }

            return buffer.ToArray();
        }
        finally
        {
            await session.ReleaseAsync(streamHandles[streamHandleIndex], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A PT_UNICODE property streamed raw is UTF-16LE with a terminating NUL that must go.</summary>
    private static string DecodeUnicodeProperty(byte[] bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        return text.TrimEnd('\0');
    }

    private static Encoding ResolveEncoding(int codepage)
    {
        if (codepage <= 0)
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(codepage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;        // legacy code pages need CodePagesEncodingProvider; the app registers it
        }
    }

    // --- Mutations ---

    public static async Task SetReadAsync(MapiSession session, ulong folderId, IReadOnlyList<ulong> messageIds, bool isRead, CancellationToken cancellationToken = default)
    {
        await OnOpenFolderAsync(session, folderId, async handles =>
        {
            var flags = isRead ? RopMessageOps.ReadFlags.SuppressReceipt : RopMessageOps.ReadFlags.ClearReadFlag;
            foreach (var batch in messageIds.Chunk(200))
            {
                var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetReadFlags(1, flags, batch), handles, cancellationToken).ConfigureAwait(false);
                RopMessageOps.ParseSetReadFlags(new RopReader(rops));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets or clears the follow-up flag (PidTagFlagStatus 2 = flagged; deleting it = not flagged is done by writing 0).</summary>
    public static async Task SetFlaggedAsync(MapiSession session, ulong folderId, ulong messageId, bool isFlagged, CancellationToken cancellationToken = default)
    {
        await OnOpenFolderAsync(session, folderId, async handles =>
        {
            var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 1, 2, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
            RopMessage.ParseOpenMessage(new RopReader(rops));
            var messageHandles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var values = new[] { TaggedPropertyValue.Long(PropertyTags.FlagStatus, isFlagged ? 2u : 0u) };
                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(2, values), messageHandles, cancellationToken).ConfigureAwait(false);
                var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
                if (problems.Count > 0)
                    throw new MapiRopException("RopSetProperties(FlagStatus)", problems[0].Error);

                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(3, 2), messageHandles, cancellationToken).ConfigureAwait(false);
                RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
            }
            finally
            {
                await session.ReleaseAsync(messageHandles[2], cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves (or copies) messages between two folders of the same mailbox.</summary>
    public static async Task<bool> MoveAsync(MapiSession session, ulong sourceFolderId, ulong destinationFolderId, IReadOnlyList<ulong> messageIds, bool copy = false, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(sourceFolderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(destinationFolderId, 0, 2), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseOpenFolder(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var partial = false;
                foreach (var batch in messageIds.Chunk(200))
                {
                    (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildMoveCopyMessages(1, 2, batch, copy), handles, cancellationToken).ConfigureAwait(false);
                    partial |= RopMessageOps.ParseMoveCopyMessages(new RopReader(rops));
                }

                return !partial;
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

    /// <summary>Hard-deletes messages from a folder. Soft delete is a move to Deleted Items.</summary>
    public static async Task<bool> DeleteAsync(MapiSession session, ulong folderId, IReadOnlyList<ulong> messageIds, CancellationToken cancellationToken = default)
    {
        var complete = true;
        await OnOpenFolderAsync(session, folderId, async handles =>
        {
            foreach (var batch in messageIds.Chunk(200))
            {
                var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildDeleteMessages(1, batch), handles, cancellationToken).ConfigureAwait(false);
                complete &= !RopMessageOps.ParseDeleteMessages(new RopReader(rops));
            }
        }, cancellationToken).ConfigureAwait(false);

        return complete;
    }

    /// <summary>Opens a folder into handle slot 1, runs the action, releases it.</summary>
    private static async Task OnOpenFolderAsync(MapiSession session, ulong folderId, Func<uint[], Task> action, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await action(handles).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }
}
