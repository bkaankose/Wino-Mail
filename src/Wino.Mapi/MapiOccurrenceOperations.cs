using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using Wino.Mapi.Wire;

namespace Wino.Mapi;

/// <summary>
/// One occurrence of a series (MS-OXOCAL 3.1.4.2 / 3.1.4.3): deleting it lists its date in the
/// master's recurrence blob; changing it adds an exception to the blob and an exception attachment
/// (an embedded IPM.OLE.CLASS message keyed by PidLidExceptionReplaceTime) to the master. The
/// series' own zone, read from its TZDEFINITION, turns the occurrence's UTC start into the wall clock
/// the blob speaks.
/// </summary>
public static class MapiOccurrenceOperations
{
    // Handle table: 0 logon, 1 master, 2 stream / attachment table / save-response slot, 3 attachment, 4 embedded message.
    private const int Slots = 5;

    private const int InlineBlobLimit = 4 * 1024;

    /// <summary>What the master holds that an occurrence edit needs: the pattern, the zone, and the series defaults to diff against.</summary>
    public sealed record MasterState(AppointmentRecurrence Recurrence, TimeZoneInfo Zone, string? Subject, string? Location, uint? BusyStatus, bool AllDay, TimeSpan Duration);

    public static async Task<MasterState> ReadMasterAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, CancellationToken cancellationToken = default)
    {
        var zoneTag = tags.TimeZoneDefinitionRecur != 0 ? tags.TimeZoneDefinitionRecur : tags.TimeZoneDefinitionStart;
        uint[] props = [tags.AppointmentRecur, zoneTag, PropertyTags.Subject, tags.Location, tags.BusyStatus, tags.AllDay, tags.TimeZoneDefinitionStart, tags.StartWhole, tags.EndWhole];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, masterId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        PropertyValue[] v;
        try
        {
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, props), handles, cancellationToken).ConfigureAwait(false);
            v = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), props);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        var blob = v[0].AsBinary ?? await MapiRulesOperations.ReadPropertyStreamAsync(session, folderId, masterId, tags.AppointmentRecur, cancellationToken).ConfigureAwait(false);
        if (blob.Length == 0)
            throw new MapiFormatException("The series master carries no recurrence pattern.");

        var keyName = MapiCalendarOperations.TimeZoneKeyName(v[1].AsBinary) ?? MapiCalendarOperations.TimeZoneKeyName(v[6].AsBinary);
        var duration = v[7].AsDateTime is { } start && v[8].AsDateTime is { } end && end > start ? end - start : TimeSpan.Zero;
        return new MasterState(AppointmentRecurrence.Parse(blob), TimeZoneDefinition.Resolve(keyName), v[2].AsString, v[3].AsString, v[4].AsUInt32, v[5].AsBoolean ?? false, duration);
    }

    /// <summary>Removes one occurrence: its date goes on the deleted list and any exception attachment for it is dropped.</summary>
    public static async Task DeleteOccurrenceAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, DateTime originalStartUtc,
        CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var master = await ReadMasterAsync(session, folderId, masterId, tags, cancellationToken).ConfigureAwait(false);
        var originalWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc), master.Zone);
        var updated = RecurrenceEncoder.WithDeletedOccurrence(master.Recurrence, originalWall);

        var handles = await OpenMasterAsync(session, folderId, masterId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (await RemoveExceptionAttachmentAsync(session, handles, tags, originalStartUtc, originalWall, cancellationToken, diagnostics).ConfigureAwait(false) > 0)
                await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);

            await WriteRecurrenceAsync(session, handles, tags, updated, cancellationToken).ConfigureAwait(false);
            await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        diagnostics?.Invoke($"series 0x{masterId:X16}: occurrence {originalStartUtc:u} deleted ({updated.DeletedInstanceDates.Count} deleted dates)");
    }

    /// <summary>
    /// Changes one occurrence: the blob gains an exception carrying what differs from the series
    /// (times always; subject, location, busy status and all-day only when they differ), and the
    /// master gains the matching exception attachment with the embedded exception message.
    /// </summary>
    public static async Task ModifyOccurrenceAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, DateTime originalStartUtc,
        MapiCalendarOperations.AppointmentWrite change, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var master = await ReadMasterAsync(session, folderId, masterId, tags, cancellationToken).ConfigureAwait(false);
        var originalWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc), master.Zone);
        var startWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(change.StartUtc, DateTimeKind.Utc), master.Zone);
        var endWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(change.EndUtc, DateTimeKind.Utc), master.Zone);

        var exception = new RecurrenceException(
            originalWall, startWall, endWall,
            Subject: string.Equals(change.Subject ?? string.Empty, master.Subject ?? string.Empty, StringComparison.Ordinal) ? null : change.Subject ?? string.Empty,
            Location: string.Equals(change.Location ?? string.Empty, master.Location ?? string.Empty, StringComparison.Ordinal) ? null : change.Location ?? string.Empty,
            BusyStatus: change.BusyStatus == (master.BusyStatus ?? 2) ? null : change.BusyStatus,
            AllDay: change.AllDay == master.AllDay ? null : change.AllDay);
        // An occurrence put back exactly where the pattern generates it, with nothing else changed, is
        // no longer an exception: the store refuses an exception identical to the instance it replaces
        // (MAPI_E_CORRUPT_DATA), so the exception and its attachment are removed instead.
        var restoresInstance = startWall == originalWall
            && (master.Duration == TimeSpan.Zero || endWall - startWall == master.Duration)
            && exception.Subject is null && exception.Location is null && exception.BusyStatus is null && exception.AllDay is null;
        var updated = restoresInstance
            ? RecurrenceEncoder.WithoutException(master.Recurrence, originalWall)
            : RecurrenceEncoder.WithException(master.Recurrence, exception);
        diagnostics?.Invoke($"series 0x{masterId:X16}: zone {master.Zone.Id}, original {originalStartUtc:u} (wall {originalWall:s}), new {change.StartUtc:u}..{change.EndUtc:u} (wall {startWall:s}..{endWall:s}), blob exceptions {master.Recurrence.Exceptions.Count} -> {updated.Exceptions.Count}, deleted {updated.DeletedInstanceDates.Count}{(restoresInstance ? ", restoring the instance" : string.Empty)}");

        var handles = await OpenMasterAsync(session, folderId, masterId, cancellationToken).ConfigureAwait(false);
        try
        {
            // Commit the removal on its own: the store validates a new exception attachment against the
            // saved ones, and a deleted-but-unsaved attachment for the same instance still counts.
            if (await RemoveExceptionAttachmentAsync(session, handles, tags, originalStartUtc, originalWall, cancellationToken, diagnostics).ConfigureAwait(false) > 0)
                await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);

            // The blob is committed before the attachment: the store checks a new exception attachment
            // against the saved pattern, and an exception whose saved times differ from the attachment
            // is refused as corrupt data.
            await WriteRecurrenceAsync(session, handles, tags, updated, cancellationToken).ConfigureAwait(false);
            await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);
            if (!restoresInstance)
            {
                await WriteExceptionAttachmentAsync(session, handles, tags, change, originalStartUtc, startWall, endWall, cancellationToken).ConfigureAwait(false);
                await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        diagnostics?.Invoke(restoresInstance
            ? $"series 0x{masterId:X16}: occurrence {originalStartUtc:u} restored to the pattern ({updated.Exceptions.Count} exceptions)"
            : $"series 0x{masterId:X16}: occurrence {originalStartUtc:u} moved to {change.StartUtc:u} ({updated.Exceptions.Count} exceptions)");
    }

    /// <summary>The attachment properties of an exception (MS-OXOCAL 2.2.10.1): hidden embedded message flagged afException, keyed by the original start.</summary>
    public static List<TaggedPropertyValue> ExceptionAttachmentProperties(MapiCalendarOperations.AppointmentWrite change, DateTime originalStartUtc, DateTime startWall, DateTime endWall)
        =>
        [
            TaggedPropertyValue.Long(PropertyTags.AttachMethod, 5),
            TaggedPropertyValue.Boolean(PropertyTags.AttachmentHidden, true),
            TaggedPropertyValue.Long(PropertyTags.AttachmentFlags, 0x2),
            TaggedPropertyValue.Long(PropertyTags.AttachmentLinkId, 0),
            TaggedPropertyValue.Long(PropertyTags.RenderingPosition, 0xFFFFFFFF),
            TaggedPropertyValue.Unicode(PropertyTags.DisplayName, change.Subject ?? string.Empty),
            TaggedPropertyValue.SysTime(PropertyTags.ExceptionStartTime, DateTime.SpecifyKind(startWall, DateTimeKind.Utc)),
            TaggedPropertyValue.SysTime(PropertyTags.ExceptionEndTime, DateTime.SpecifyKind(endWall, DateTimeKind.Utc)),
            TaggedPropertyValue.SysTime(PropertyTags.ExceptionReplaceTime, DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc)),
        ];

    /// <summary>The embedded exception message (MS-OXOCAL 2.2.10.1.1): the occurrence as an appointment under the exception class, tied to its original start.</summary>
    public static List<TaggedPropertyValue> ExceptionMessageProperties(MapiCalendarTags tags, MapiCalendarOperations.AppointmentWrite change, DateTime originalStartUtc)
    {
        var values = new List<TaggedPropertyValue>
        {
            TaggedPropertyValue.Unicode(PropertyTags.MessageClass, PropertyTags.ExceptionMessageClass),
            TaggedPropertyValue.Unicode(PropertyTags.Subject, change.Subject ?? string.Empty),
            TaggedPropertyValue.Unicode(tags.Location, change.Location ?? string.Empty),
            TaggedPropertyValue.SysTime(tags.StartWhole, change.StartUtc),
            TaggedPropertyValue.SysTime(tags.EndWhole, change.EndUtc),
            TaggedPropertyValue.Boolean(tags.AllDay, change.AllDay),
            TaggedPropertyValue.Long(tags.BusyStatus, change.BusyStatus),
            TaggedPropertyValue.Boolean(tags.Recurring, false),
            TaggedPropertyValue.Boolean(tags.ReminderSet, change.ReminderMinutes is not null),
        };
        if (change.ReminderMinutes is { } minutes) values.Add(TaggedPropertyValue.Long(tags.ReminderDelta, (uint)Math.Max(0, minutes)));
        if (tags.ExceptionReplaceTime != 0) values.Add(TaggedPropertyValue.SysTime(tags.ExceptionReplaceTime, DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc)));
        values.AddRange(MapiCalendarOperations.BodyProperties(change.Body));
        return values;
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    private static async Task<uint[]> OpenMasterAsync(MapiSession session, ulong folderId, ulong masterId, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, masterId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        return MapiSession.MergeHandles(handles, returned, Slots);
    }

    private static async Task SaveMasterAsync(MapiSession session, uint[] handles, CancellationToken cancellationToken)
    {
        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
    }

    private static async Task WriteRecurrenceAsync(MapiSession session, uint[] handles, MapiCalendarTags tags, AppointmentRecurrence recurrence, CancellationToken cancellationToken)
    {
        var blob = RecurrenceEncoder.Encode(recurrence);
        if (blob.Length <= InlineBlobLimit)
        {
            await SetPropertiesAsync(session, handles, 1, [TaggedPropertyValue.Binary(tags.AppointmentRecur, blob)], cancellationToken).ConfigureAwait(false);
            return;
        }

        await MapiMessageComposer.WritePropertyAsync(session, handles, 1, 2, tags.AppointmentRecur, blob, isUnicodeString: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the exception attachment whose PidTagExceptionReplaceTime is this occurrence's original start, if there is one.</summary>
    /// <returns>How many exception attachments were removed.</returns>
    private static async Task<int> RemoveExceptionAttachmentAsync(MapiSession session, uint[] handles, MapiCalendarTags tags, DateTime originalStartUtc, DateTime originalStartWall,
        CancellationToken cancellationToken, Action<string>? diagnostics = null)
    {
        // The attachment carries PidTagExceptionReplaceTime when this client wrote it; exceptions
        // written by Outlook or OWA may only key the embedded message (PidLidExceptionReplaceTime),
        // and some writers store the wall clock rather than UTC. Match on the attachment first, and
        // open the embedded message of any other exception attachment to read its key. A missed match
        // leaves two exceptions for one instance, which the store rejects as corrupt data.
        uint[] columns = [PropertyTags.AttachNumber, PropertyTags.ExceptionReplaceTime, PropertyTags.AttachmentFlags, PropertyTags.AttachMethod];
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(1, 2), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
        var tableHandles = MapiSession.MergeHandles(handles, returned, Slots);

        var toDelete = new List<uint>();
        var toInspect = new List<uint>();
        try
        {
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), tableHandles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseSetColumns(new RopReader(rops));
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 200), tableHandles, cancellationToken).ConfigureAwait(false);
            foreach (var row in RopFolder.ParseQueryRows(new RopReader(rops), columns))
            {
                if (row[0].AsUInt32 is not { } number)
                    continue;

                var replace = row[1].Value as DateTime?;
                var isException = (row[2].AsUInt32 & 0x2) != 0 || row[3].AsUInt32 == 5;
                diagnostics?.Invoke($"attachment {number}: ExceptionReplaceTime {(replace is { } r ? r.ToString("u") : "-")}, flags 0x{row[2].AsUInt32 ?? 0:X}, method {row[3].AsUInt32 ?? 0}");

                if (replace is { } known && IsSameInstant(known, originalStartUtc, originalStartWall))
                    toDelete.Add(number);
                else if (isException)
                    toInspect.Add(number);
            }
        }
        finally
        {
            await session.ReleaseAsync(tableHandles[2], cancellationToken).ConfigureAwait(false);
        }

        foreach (var number in toInspect)
        {
            if (await ReadEmbeddedReplaceTimeAsync(session, handles, tags, number, cancellationToken).ConfigureAwait(false) is { } embedded)
            {
                diagnostics?.Invoke($"attachment {number}: embedded PidLidExceptionReplaceTime {embedded:u}");
                if (IsSameInstant(embedded, originalStartUtc, originalStartWall))
                    toDelete.Add(number);
            }
        }

        foreach (var number in toDelete)
        {
            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildDeleteAttachment(1, number), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseDeleteAttachment(new RopReader(rops));
            diagnostics?.Invoke($"attachment {number}: removed (exception for {originalStartUtc:u})");
        }

        return toDelete.Count;
    }

    private static bool IsSameInstant(DateTime candidate, DateTime originalStartUtc, DateTime originalStartWall)
        => Math.Abs((candidate - originalStartUtc).TotalMinutes) < 1 || Math.Abs((candidate - originalStartWall).TotalMinutes) < 1;

    /// <summary>The exception key on the embedded message of an exception attachment, or null when it has none.</summary>
    private static async Task<DateTime?> ReadEmbeddedReplaceTimeAsync(MapiSession session, uint[] handles, MapiCalendarTags tags, uint attachmentNumber, CancellationToken cancellationToken)
    {
        if (tags.ExceptionReplaceTime == 0)
            return null;

        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildOpenAttachment(1, 3, attachmentNumber), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseOpenAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildOpenEmbeddedMessage(3, 4, create: false), attachmentHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseOpenEmbeddedMessage(new RopReader(rops));
            var embeddedHandles = MapiSession.MergeHandles(attachmentHandles, returned, Slots);
            try
            {
                uint[] props = [tags.ExceptionReplaceTime];
                (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(4, props), embeddedHandles, cancellationToken).ConfigureAwait(false);
                return RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), props)[0].Value as DateTime?;
            }
            finally
            {
                await session.ReleaseAsync(embeddedHandles[4], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (MapiRopException)
        {
            return null;
        }
        finally
        {
            await session.ReleaseAsync(attachmentHandles[3], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteExceptionAttachmentAsync(MapiSession session, uint[] handles, MapiCalendarTags tags, MapiCalendarOperations.AppointmentWrite change,
        DateTime originalStartUtc, DateTime startWall, DateTime endWall, CancellationToken cancellationToken)
    {
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateAttachment(1, 3), handles, cancellationToken).ConfigureAwait(false);
        RopMessageWrite.ParseCreateAttachment(new RopReader(rops));
        var attachmentHandles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            await SetPropertiesAsync(session, attachmentHandles, 3, ExceptionAttachmentProperties(change, originalStartUtc, startWall, endWall), cancellationToken).ConfigureAwait(false);

            (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildOpenEmbeddedMessage(3, 4, create: true), attachmentHandles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseOpenEmbeddedMessage(new RopReader(rops));
            var embeddedHandles = MapiSession.MergeHandles(attachmentHandles, returned, Slots);

            try
            {
                await SetPropertiesAsync(session, embeddedHandles, 4, ExceptionMessageProperties(tags, change, originalStartUtc), cancellationToken).ConfigureAwait(false);
                (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 4), embeddedHandles, cancellationToken).ConfigureAwait(false);
                RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
            }
            finally
            {
                await session.ReleaseAsync(embeddedHandles[4], cancellationToken).ConfigureAwait(false);
            }

            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildSaveChangesAttachment(2, 3), attachmentHandles, cancellationToken).ConfigureAwait(false);
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
            throw new MapiFormatException($"The store refused occurrence properties: {string.Join(", ", problems.Select(p => $"{PropertyTags.Describe(p.Tag)}=0x{p.Error:X8}"))}");
    }
}
