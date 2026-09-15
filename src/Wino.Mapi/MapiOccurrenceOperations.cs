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
    public sealed record MasterState(AppointmentRecurrence Recurrence, TimeZoneInfo Zone, string? Subject, string? Location, uint? BusyStatus, bool AllDay);

    public static async Task<MasterState> ReadMasterAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, CancellationToken cancellationToken = default)
    {
        var zoneTag = tags.TimeZoneDefinitionRecur != 0 ? tags.TimeZoneDefinitionRecur : tags.TimeZoneDefinitionStart;
        uint[] props = [tags.AppointmentRecur, zoneTag, PropertyTags.Subject, tags.Location, tags.BusyStatus, tags.AllDay, tags.TimeZoneDefinitionStart];

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
        return new MasterState(AppointmentRecurrence.Parse(blob), TimeZoneDefinition.Resolve(keyName), v[2].AsString, v[3].AsString, v[4].AsUInt32, v[5].AsBoolean ?? false);
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
            await RemoveExceptionAttachmentAsync(session, handles, originalStartUtc, cancellationToken).ConfigureAwait(false);
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
        var updated = RecurrenceEncoder.WithException(master.Recurrence, exception);

        var handles = await OpenMasterAsync(session, folderId, masterId, cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveExceptionAttachmentAsync(session, handles, originalStartUtc, cancellationToken).ConfigureAwait(false);
            await WriteRecurrenceAsync(session, handles, tags, updated, cancellationToken).ConfigureAwait(false);
            await WriteExceptionAttachmentAsync(session, handles, tags, change, originalStartUtc, startWall, endWall, cancellationToken).ConfigureAwait(false);
            await SaveMasterAsync(session, handles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }

        diagnostics?.Invoke($"series 0x{masterId:X16}: occurrence {originalStartUtc:u} moved to {change.StartUtc:u} ({updated.Exceptions.Count} exceptions)");
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
    private static async Task RemoveExceptionAttachmentAsync(MapiSession session, uint[] handles, DateTime originalStartUtc, CancellationToken cancellationToken)
    {
        uint[] columns = [PropertyTags.AttachNumber, PropertyTags.ExceptionReplaceTime];
        var (rops, returned) = await session.ExecuteAsync(RopMessageOps.BuildGetAttachmentTable(1, 2), handles, cancellationToken).ConfigureAwait(false);
        RopMessageOps.ParseGetAttachmentTable(new RopReader(rops));
        var tableHandles = MapiSession.MergeHandles(handles, returned, Slots);

        var toDelete = new List<uint>();
        try
        {
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), tableHandles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseSetColumns(new RopReader(rops));
            (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 200), tableHandles, cancellationToken).ConfigureAwait(false);
            foreach (var row in RopFolder.ParseQueryRows(new RopReader(rops), columns))
            {
                if (row[0].AsUInt32 is { } number && row[1].Value is DateTime replace && Math.Abs((replace - originalStartUtc).TotalMinutes) < 1)
                    toDelete.Add(number);
            }
        }
        finally
        {
            await session.ReleaseAsync(tableHandles[2], cancellationToken).ConfigureAwait(false);
        }

        foreach (var number in toDelete)
        {
            (rops, _) = await session.ExecuteAsync(RopMessageWrite.BuildDeleteAttachment(1, number), handles, cancellationToken).ConfigureAwait(false);
            RopMessageWrite.ParseDeleteAttachment(new RopReader(rops));
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
