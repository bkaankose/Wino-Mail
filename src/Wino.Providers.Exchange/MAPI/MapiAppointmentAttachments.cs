using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>
/// A file the user attached to an appointment, from its attachments table. <see cref="ExceptionAttachNumber"/> is set
/// when the file belongs to a changed occurrence: the number of the series' Exception Attachment object whose embedded
/// message holds it.
/// </summary>
public sealed record MapiAppointmentAttachment(uint AttachNumber, string FileName, string? MimeType, uint Size, DateTime? LastModified, uint? ExceptionAttachNumber = null);

/// <summary>The files of one changed occurrence, keyed by the start the occurrence originally had (UTC).</summary>
public sealed record MapiExceptionAttachments(DateTime OriginalStartUtc, List<MapiAppointmentAttachment> Files);

/// <summary>
/// The user's file attachments of an appointment (MS-OXCMSG 2.2.2): attachments by value (PidTagAttachMethod 1)
/// that are neither hidden nor the Exception Attachment objects a series keeps its changed occurrences in
/// (MS-OXOCAL 2.2.10.1: hidden, PidTagAttachmentFlags afException). A changed occurrence's own files are on the
/// embedded message of its Exception Attachment object (MS-OXOCAL 2.2.10.2).
/// </summary>
public static class MapiAppointmentAttachments
{
    // Handle table: 0 logon, 1 message, 2 attachment table or attachment, 3 attachment stream or embedded message,
    // 4 save response or the embedded message's attachment table or attachment, 5 its stream.
    private const int Slots = 6;

    private const uint AttachByValue = 1;
    private const uint AttachEmbeddedMessage = 5;
    private const uint ExceptionFlag = 0x2;

    private static readonly uint[] Columns =
    [
        PropertyTags.AttachNumber, PropertyTags.AttachMethod, PropertyTags.AttachSize, PropertyTags.AttachLongFilename, PropertyTags.AttachFilename,
        PropertyTags.DisplayName, PropertyTags.AttachMimeTag, PropertyTags.AttachmentHidden, PropertyTags.AttachmentFlags, PropertyTags.LastModificationTime
    ];

    private static readonly uint[] ExceptionColumns = [PropertyTags.AttachNumber, PropertyTags.AttachMethod, PropertyTags.AttachmentFlags, PropertyTags.ExceptionReplaceTime];

    /// <summary>The file attachments of an appointment, without their content.</summary>
    public static async Task<List<MapiAppointmentAttachment>> ReadAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var handles = await OpenMessageAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadTableAsync(session, handles, 1, 2, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The files of every changed occurrence of a series, without their content. The occurrence is identified by its
    /// Exception Attachment object's PidTagExceptionReplaceTime (MS-OXOCAL 2.2.10.1.6, UTC), or the embedded message's
    /// PidLidExceptionReplaceTime (<paramref name="embeddedReplaceTimeTag"/>) where a writer left the former out.
    /// </summary>
    public static async Task<List<MapiExceptionAttachments>> ReadExceptionsAsync(MapiSession session, ulong folderId, ulong masterId, uint embeddedReplaceTimeTag,
        CancellationToken cancellationToken = default)
    {
        var handles = await OpenMessageAsync(session, folderId, masterId, cancellationToken).ConfigureAwait(false);
        try
        {
            var exceptions = new List<(uint Number, DateTime? ReplaceTime)>();
            var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(1, 2), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
            var tableHandles = MapiSession.MergeHandles(handles, returned, Slots);
            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, ExceptionColumns), tableHandles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));
                while (true)
                {
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 100), tableHandles, cancellationToken).ConfigureAwait(false);
                    var rows = RopFolder.ParseQueryRows(new RopReader(rops), ExceptionColumns);
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (row[0].AsUInt32 is { } number && row[1].AsUInt32 == AttachEmbeddedMessage && ((row[2].AsUInt32 ?? 0) & ExceptionFlag) != 0)
                            exceptions.Add((number, row[3].Value as DateTime?));
                    }
                }
            }
            finally
            {
                await session.ReleaseAsync(tableHandles[2], cancellationToken).ConfigureAwait(false);
            }

            var result = new List<MapiExceptionAttachments>();
            foreach (var (number, replaceTime) in exceptions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var embeddedHandles = await OpenExceptionMessageAsync(session, handles, number, cancellationToken).ConfigureAwait(false);
                try
                {
                    var originalStart = replaceTime;
                    if (originalStart == null && embeddedReplaceTimeTag != 0)
                    {
                        uint[] props = [embeddedReplaceTimeTag];
                        (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(3, props), embeddedHandles, cancellationToken).ConfigureAwait(false);
                        originalStart = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), props)[0].Value as DateTime?;
                    }

                    if (originalStart is { } start)
                        result.Add(new MapiExceptionAttachments(DateTime.SpecifyKind(start, DateTimeKind.Utc),
                            await ReadTableAsync(session, embeddedHandles, 3, 4, number, cancellationToken).ConfigureAwait(false)));
                }
                finally
                {
                    await session.ReleaseAsync(embeddedHandles[3], cancellationToken).ConfigureAwait(false);
                    await session.ReleaseAsync(embeddedHandles[2], cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The content of one attachment (PidTagAttachDataBinary), streamed when it is too large to come back inline. With
    /// <paramref name="exceptionAttachNumber"/> the attachment is read from that changed occurrence's embedded message.
    /// </summary>
    public static async Task<byte[]> ReadContentAsync(MapiSession session, ulong folderId, ulong messageId, uint attachNumber, uint? exceptionAttachNumber = null,
        CancellationToken cancellationToken = default)
    {
        var handles = await OpenMessageAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (exceptionAttachNumber is not { } exceptionNumber)
                return await ReadContentAsync(session, handles, 1, 2, 3, attachNumber, cancellationToken).ConfigureAwait(false);

            var embeddedHandles = await OpenExceptionMessageAsync(session, handles, exceptionNumber, cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadContentAsync(session, embeddedHandles, 3, 4, 5, attachNumber, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await session.ReleaseAsync(embeddedHandles[3], cancellationToken).ConfigureAwait(false);
                await session.ReleaseAsync(embeddedHandles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Every file attachment of an appointment with its content, for the meeting request that carries them.</summary>
    public static async Task<List<MapiOutgoingAttachment>> ReadFilesAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
    {
        var handles = await OpenMessageAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
        try
        {
            var files = new List<MapiOutgoingAttachment>();
            foreach (var attachment in await ReadTableAsync(session, handles, 1, 2, null, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await ReadContentAsync(session, handles, 1, 2, 3, attachment.AttachNumber, cancellationToken).ConfigureAwait(false);
                files.Add(new MapiOutgoingAttachment(attachment.FileName, attachment.MimeType ?? "application/octet-stream", content, null, false));
            }

            return files;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes and creates attachments on the message open at handle index 1 (RopDeleteAttachment, RopCreateAttachment,
    /// RopSaveChangesAttachment). They are committed with the message's own RopSaveChangesMessage (MS-OXCMSG 3.2.5.14).
    /// </summary>
    internal static async Task ApplyAsync(MapiSession session, uint[] messageHandles, IReadOnlyList<uint> removed, IReadOnlyList<MapiOutgoingAttachment> added, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles(messageHandles, [], Slots);

        foreach (var number in removed)
        {
            var (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildDeleteAttachment(1, number), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseDeleteAttachment(new RopReader(rops));
        }

        foreach (var attachment in added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateAttachment(1, 2), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseCreateAttachment(new RopReader(rops));
            var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var properties = new List<TaggedPropertyValue>
                {
                    TaggedPropertyValue.Long(PropertyTags.AttachMethod, AttachByValue),
                    TaggedPropertyValue.Unicode(PropertyTags.AttachLongFilename, attachment.FileName),
                    TaggedPropertyValue.Unicode(PropertyTags.AttachFilename, attachment.FileName),
                    TaggedPropertyValue.Unicode(PropertyTags.DisplayName, attachment.FileName),
                    TaggedPropertyValue.Unicode(PropertyTags.AttachMimeTag, attachment.MimeType),
                    TaggedPropertyValue.Boolean(PropertyTags.AttachmentHidden, false),
                };
                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSetProperties(2, properties), attachmentHandles, cancellationToken).ConfigureAwait(false);
                var problems = RopMessageOps.ParseSetProperties(new RopReader(rops));
                if (problems.Count > 0)
                    throw new MapiRopException($"RopSetProperties({PropertyTags.Describe(problems[0].Tag)})", problems[0].Error);

                await MapiMessageComposer.WritePropertyAsync(session, attachmentHandles, 2, 3, PropertyTags.AttachDataBinary, attachment.Data, isUnicodeString: false, cancellationToken).ConfigureAwait(false);

                (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildSaveChangesAttachment(4, 2), attachmentHandles, cancellationToken).ConfigureAwait(false);
                RopMessageWrite.ParseSaveChangesAttachment(new RopReader(rops));
            }
            finally
            {
                await session.ReleaseAsync(attachmentHandles[2], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<uint[]> OpenMessageAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        return MapiSession.MergeHandles(handles, returned, Slots);
    }

    /// <summary>Opens the Exception Attachment object at index 2 and its embedded message at index 3; the caller releases both.</summary>
    private static async Task<uint[]> OpenExceptionMessageAsync(MapiSession session, uint[] handles, uint exceptionAttachNumber, CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildOpenAttachment(1, 2, exceptionAttachNumber), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseOpenAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildOpenEmbeddedMessageReadOnly(2, 3), attachmentHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseOpenEmbeddedMessage(new RopReader(rops));
            return MapiSession.MergeHandles(attachmentHandles, returned, Slots);
        }
        catch
        {
            await session.ReleaseAsync(attachmentHandles[2], cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<List<MapiAppointmentAttachment>> ReadTableAsync(MapiSession session, uint[] handles, byte messageIndex, byte tableIndex, uint? exceptionAttachNumber,
        CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(messageIndex, tableIndex), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
        var tableHandles = MapiSession.MergeHandles(handles, returned, Slots);

        var attachments = new List<MapiAppointmentAttachment>();
        try
        {
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(tableIndex, Columns), tableHandles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseSetColumns(new RopReader(rops));

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(tableIndex, 50), tableHandles, cancellationToken).ConfigureAwait(false);
                var rows = RopFolder.ParseQueryRows(new RopReader(rops), Columns);
                if (rows.Count == 0)
                    break;

                foreach (var row in rows)
                {
                    if (row[0].AsUInt32 is not { } number || row[1].AsUInt32 != AttachByValue)
                        continue;
                    if (row[7].AsBoolean == true || ((row[8].AsUInt32 ?? 0) & ExceptionFlag) != 0)
                        continue;

                    var name = row[3].AsString ?? row[4].AsString ?? row[5].AsString;
                    attachments.Add(new MapiAppointmentAttachment(number, string.IsNullOrWhiteSpace(name) ? "attachment" : name, row[6].AsString, row[2].AsUInt32 ?? 0,
                        row[9].Value as DateTime?, exceptionAttachNumber));
                }
            }
        }
        finally
        {
            await session.ReleaseAsync(tableHandles[tableIndex], cancellationToken).ConfigureAwait(false);
        }

        return attachments;
    }

    private static async Task<byte[]> ReadContentAsync(MapiSession session, uint[] handles, byte messageIndex, byte attachmentIndex, byte streamIndex, uint attachNumber,
        CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildOpenAttachment(messageIndex, attachmentIndex, attachNumber), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseOpenAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            uint[] tags = [PropertyTags.AttachDataBinary];
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(attachmentIndex, tags), attachmentHandles, cancellationToken).ConfigureAwait(false);
            var cell = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags)[0];
            if (cell.Error is null && cell.AsBinary is { } inline)
                return inline;
            if (cell.Error != MapiRopException.NotEnoughMemory)
                throw new MapiRopException("RopGetPropertiesSpecific(PidTagAttachDataBinary)", cell.Error ?? MapiRopException.NotFound);

            (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenStream(PropertyTags.AttachDataBinary, attachmentIndex, streamIndex), attachmentHandles, cancellationToken).ConfigureAwait(false);
            var size = RopMessage.ParseOpenStream(new RopReader(rops));
            var streamHandles = MapiSession.MergeHandles(attachmentHandles, returned, Slots);
            try
            {
                using var buffer = new MemoryStream((int)Math.Min(size, 64 * 1024 * 1024));
                while (buffer.Length < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var want = Math.Min(RopMessage.SafeReadChunk, size - (uint)buffer.Length);
                    (rops, _) = await session.ExecuteAsync(RopMessage.BuildReadStream(streamIndex, want), streamHandles, cancellationToken).ConfigureAwait(false);
                    var data = RopMessage.ParseReadStream(new RopReader(rops));
                    if (data.Length == 0)
                        throw new MapiFormatException($"The attachment stream ended at {buffer.Length} of {size} bytes.");
                    buffer.Write(data);
                }

                return buffer.ToArray();
            }
            finally
            {
                await session.ReleaseAsync(streamHandles[streamIndex], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(attachmentHandles[attachmentIndex], cancellationToken).ConfigureAwait(false);
        }
    }
}
