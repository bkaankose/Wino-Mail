using System.Text;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>An outgoing message, provider-neutral: what a compose window produces.</summary>
public sealed class MapiOutgoingMessage
{
    public string Subject { get; set; } = string.Empty;
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public uint Importance { get; set; } = 1;
    public string? InternetMessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public bool ReadReceiptRequested { get; set; }
    public List<RopMessageWrite.Recipient> Recipients { get; } = [];
    public List<MapiOutgoingAttachment> Attachments { get; } = [];

    /// <summary>IPM.Note unless the message is something else (a meeting response, say).</summary>
    public string MessageClass { get; set; } = "IPM.Note";

    /// <summary>Properties set beside the standard ones (meeting identity on a response, for instance).</summary>
    public List<TaggedPropertyValue> ExtraProperties { get; } = [];
}

public sealed record MapiOutgoingAttachment(string FileName, string MimeType, byte[] Data, string? ContentId, bool IsInline);

/// <summary>
/// Creates messages on the server from <see cref="MapiOutgoingMessage"/> (rung 5): drafts, and the
/// message that RopSubmitMessage sends. Everything larger than a few KB goes through a write stream;
/// RopSetProperties has the same ~8KB ceiling as its read counterpart.
/// </summary>
public static class MapiMessageComposer
{
    // Handle table: 0 logon, 1 message, 2 stream, 3 attachment, 4 attachment stream.
    private const int Slots = 5;

    /// <summary>Inline this many bytes at most; above it, stream. Comfortably under the ROP buffer.</summary>
    private const int InlineLimit = 4 * 1024;

    /// <summary>Creates and saves a message in <paramref name="folderId"/>; returns its id.</summary>
    public static async Task<ulong> CreateAsync(MapiSession session, ulong folderId, MapiOutgoingMessage message, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, folderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await PopulateAsync(session, handles, message, cancellationToken, diagnostics).ConfigureAwait(false);

            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
            var savedId = RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
            diagnostics?.Invoke($"compose: created 0x{savedId:X16} in 0x{folderId:X16} ({message.Recipients.Count} recipients, {message.Attachments.Count} attachments)");
            return savedId != 0 ? savedId : createdId ?? 0;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the message in <paramref name="outboxOrDraftsFolderId"/>, marks it to be filed to
    /// <paramref name="sentItemsFolderId"/> and deleted after submit, saves, and submits it.
    /// </summary>
    public static async Task SendAsync(MapiSession session, ulong outboxOrDraftsFolderId, ulong sentItemsFolderId, MapiOutgoingMessage message, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, outboxOrDraftsFolderId), handles, cancellationToken).ConfigureAwait(false);
        RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await PopulateAsync(session, handles, message, cancellationToken, diagnostics).ConfigureAwait(false);

            // PidTagSentMailSvrEID makes the store MOVE the submitted message into Sent Items once the
            // transport has it. PidTagDeleteAfterSubmit would delete it instead (for clients that keep their
            // own copy), and the first live send showed exactly that: delivered, no Sent Items copy.
            var sendProperties = new[]
            {
                TaggedPropertyValue.Binary(RopMessageWrite.SentMailSvrEID, RopMessageWrite.FolderServerId(sentItemsFolderId)),
            };
            await SetPropertiesAsync(session, handles, 1, sendProperties, cancellationToken).ConfigureAwait(false);

            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1, RopMessageOps.SaveFlags.KeepOpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
            var savedId = RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));

            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildSubmitMessage(1), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseSubmitMessage(new RopReader(rops));
            diagnostics?.Invoke($"compose: submitted 0x{savedId:X16} ({message.Recipients.Count} recipients, {message.Attachments.Count} attachments)");
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PopulateAsync(MapiSession session, uint[] handles, MapiOutgoingMessage message, CancellationToken cancellationToken, Action<string>? diagnostics)
    {
        var small = new List<TaggedPropertyValue>
        {
            TaggedPropertyValue.Unicode(PropertyTags.MessageClass, message.MessageClass),
            TaggedPropertyValue.Unicode(PropertyTags.Subject, message.Subject),
            TaggedPropertyValue.Long(PropertyTags.Importance, message.Importance),
            TaggedPropertyValue.Long(PropertyTags.InternetCodepage, 65001),
            TaggedPropertyValue.Boolean(PropertyTags.ReadReceiptRequested, message.ReadReceiptRequested),
        };

        if (!string.IsNullOrEmpty(message.InternetMessageId))
            small.Add(TaggedPropertyValue.Unicode(PropertyTags.InternetMessageId, message.InternetMessageId));
        if (!string.IsNullOrEmpty(message.InReplyTo))
            small.Add(TaggedPropertyValue.Unicode(PropertyTags.InReplyToId, message.InReplyTo));
        if (!string.IsNullOrEmpty(message.References))
            small.Add(TaggedPropertyValue.Unicode(PropertyTags.InternetReferences, message.References));
        small.AddRange(message.ExtraProperties);

        await SetPropertiesAsync(session, handles, 1, small, cancellationToken).ConfigureAwait(false);

        if (message.TextBody is { } text)
            await WritePropertyAsync(session, handles, 1, 2, PropertyTags.Body, Encoding.Unicode.GetBytes(text + "\0"), isUnicodeString: true, cancellationToken).ConfigureAwait(false);

        if (message.HtmlBody is { } html)
            await WritePropertyAsync(session, handles, 1, 2, PropertyTags.Html, Encoding.UTF8.GetBytes(html), isUnicodeString: false, cancellationToken).ConfigureAwait(false);

        if (message.Recipients.Count > 0)
        {
            var (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildModifyRecipients(1, message.Recipients), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseModifyRecipients(new RopReader(rops));
        }

        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddAttachmentAsync(session, handles, attachment, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddAttachmentAsync(MapiSession session, uint[] handles, MapiOutgoingAttachment attachment, CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateAttachment(1, 3), handles, cancellationToken).ConfigureAwait(false);
        RopMessageWrite.ParseCreateAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var properties = new List<TaggedPropertyValue>
            {
                TaggedPropertyValue.Long(PropertyTags.AttachMethod, 1),                 // by value
                TaggedPropertyValue.Unicode(PropertyTags.AttachLongFilename, attachment.FileName),
                TaggedPropertyValue.Unicode(PropertyTags.AttachFilename, attachment.FileName),
                TaggedPropertyValue.Unicode(PropertyTags.DisplayName, attachment.FileName),
                TaggedPropertyValue.Unicode(PropertyTags.AttachMimeTag, attachment.MimeType),
                TaggedPropertyValue.Boolean(PropertyTags.AttachmentHidden, attachment.IsInline),
            };

            if (!string.IsNullOrEmpty(attachment.ContentId))
                properties.Add(TaggedPropertyValue.Unicode(PropertyTags.AttachContentId, attachment.ContentId));

            await SetPropertiesAsync(session, attachmentHandles, 3, properties, cancellationToken).ConfigureAwait(false);
            await WritePropertyAsync(session, attachmentHandles, 3, 4, PropertyTags.AttachDataBinary, attachment.Data, isUnicodeString: false, cancellationToken).ConfigureAwait(false);

            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildSaveChangesAttachment(4, 3), attachmentHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseSaveChangesAttachment(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(attachmentHandles[3], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SetPropertiesAsync(MapiSession session, uint[] handles, byte handleIndex, IReadOnlyList<TaggedPropertyValue> values, CancellationToken cancellationToken)
    {
        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(handleIndex, values), handles, cancellationToken).ConfigureAwait(false);
        var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
        if (problems.Count > 0)
            throw new MapiRopException($"RopSetProperties({PropertyTags.Describe(problems[0].Tag)})", problems[0].Error);
    }

    /// <summary>
    /// Writes one property: inline when small, else through a stream opened for create. A Unicode
    /// string property written inline is a UnicodeZ value; the same bytes streamed carry their NUL.
    /// </summary>
    internal static async Task WritePropertyAsync(MapiSession session, uint[] handles, byte objectHandleIndex, byte streamHandleIndex, uint tag, byte[] bytes, bool isUnicodeString, CancellationToken cancellationToken)
    {
        if (bytes.Length <= InlineLimit)
        {
            TaggedPropertyValue value = isUnicodeString
                ? TaggedPropertyValue.Unicode(tag, Encoding.Unicode.GetString(bytes).TrimEnd('\0'))
                : TaggedPropertyValue.Binary(tag, bytes);
            await SetPropertiesAsync(session, handles, objectHandleIndex, [value], cancellationToken).ConfigureAwait(false);
            return;
        }

        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenStream(tag, objectHandleIndex, streamHandleIndex, RopMessageWrite.OpenStreamCreate), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenStream(new RopReader(rops));
        var streamHandles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = bytes.AsSpan(offset, Math.Min(24 * 1024, bytes.Length - offset));
                (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildWriteStream(streamHandleIndex, chunk), streamHandles, cancellationToken).ConfigureAwait(false);
                var written = RopMessageWrite.ParseWriteStream(new RopReader(rops));
                if (written == 0)
                    throw new MapiFormatException("RopWriteStream wrote nothing; the stream is not accepting data.");
                offset += written;
            }

            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildCommitStream(streamHandleIndex), streamHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseCommitStream(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(streamHandles[streamHandleIndex], cancellationToken).ConfigureAwait(false);
        }
    }
}
