using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Wire;

namespace Wino.Providers.Exchange.MAPI;

/// <summary>The store's tags for the task named properties (PSETID_Task, PSETID_Common, PS_PUBLIC_STRINGS), resolved once per session.</summary>
public sealed record MapiTaskTags(
    uint TaskStatus, uint PercentComplete, uint TaskStartDate, uint TaskDueDate, uint TaskDateCompleted, uint TaskComplete,
    uint ReminderSet, uint ReminderTime, uint CommonStart, uint CommonEnd,
    uint ReminderSignalTime, uint ReminderDelta, uint Keywords);

/// <summary>One row of a Tasks folder, as this client reads it.</summary>
public sealed record MapiTaskInfo(
    ulong MessageId,
    string MessageClass,
    string? Subject,
    string? Body,
    DateTime? StartDate,
    DateTime? DueDate,
    DateTime? CompletedDate,
    uint? Importance,
    uint? Status,
    double? PercentComplete,
    bool? IsComplete,
    bool? ReminderSet,
    DateTime? ReminderTime,
    string[]? Categories)
{
    public const uint StatusNotStarted = 0;
    public const uint StatusInProgress = 1;
    public const uint StatusComplete = 2;

    public bool IsTask => MessageClass.StartsWith(PropertyTags.TaskMessageClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>Complete when the flag says so, else when the status does (the two are kept in step by every client).</summary>
    public bool Completed => IsComplete ?? Status == StatusComplete;
}

/// <summary>What a task holds on the server before a write, for the fields the local model cannot represent.</summary>
public sealed record MapiTaskState(DateTime? StartDate, uint? Importance, uint? Status);

/// <summary>The fields this client writes to a task.</summary>
public sealed class MapiTaskWrite
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
    /// <summary>The due day (date part only).</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>The start day (date part only).</summary>
    public DateTime? StartDate { get; set; }

    public uint Importance { get; set; } = 1;
    public bool IsComplete { get; set; }
    public DateTime? CompletedDate { get; set; }

    /// <summary>When the reminder signals, in UTC; null for no reminder.</summary>
    public DateTime? ReminderAtUtc { get; set; }

    public string[] Categories { get; set; } = [];

    /// <summary>
    /// Whether the local task's start date, reminder and categories are the server's. A field the task capabilities
    /// keep local is left as the server has it, so the write does not clear what another client set.
    /// </summary>
    public bool WritesStartDate { get; set; }
    public bool WritesReminder { get; set; }
    public bool WritesCategories { get; set; }
}

/// <summary>
/// Tasks over MAPI: the Tasks folder by its well-known entry id on the Inbox, rows from its
/// contents table with the MS-OXOTASK named properties, and create / update as IPM.Task.
/// </summary>
public static class MapiTaskOperations
{
    private const int Slots = 3;

    public const uint ImportanceLow = 0;
    public const uint ImportanceNormal = 1;
    public const uint ImportanceHigh = 2;

    /// <summary>PidLidReminderSignalTime (MS-OXORMDR 2.2.1.2), PSETID_Common.</summary>
    private const uint LidReminderSignalTime = 0x8560;

    /// <summary>
    /// The "no date" value of PidLidTaskStartDate and PidLidTaskDueDate (MS-OXOTASK 2.2.2.2.4): 0x5AE980E0 minutes
    /// after 1601-01-01, which is 4501-01-01.
    /// </summary>
    private static readonly DateTime NoTaskDate = new(4501, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static async Task<MapiTaskTags> ResolveTagsAsync(MapiSession session, CancellationToken cancellationToken = default)
    {
        var names = new List<RopProperties.PropertyName>
        {
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidTaskStatus),
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidPercentComplete),
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidTaskStartDate),
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidTaskDueDate),
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidTaskDateCompleted),
            RopProperties.PropertyName.ById(PropertyTags.TaskPropertySet, PropertyTags.LidTaskComplete),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidReminderSet),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidReminderTime),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidCommonStart),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidCommonEnd),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, LidReminderSignalTime),
            RopProperties.PropertyName.ById(PropertyTags.CommonPropertySet, PropertyTags.LidReminderDelta),
            RopProperties.PropertyName.ByName(PropertyTags.PublicStringsPropertySet, PropertyTags.KeywordsPropertyName),
        };
        ushort[] types =
        [
            PropertyTypes.Long, PropertyTypes.Double, PropertyTypes.SysTime, PropertyTypes.SysTime, PropertyTypes.SysTime, PropertyTypes.Boolean,
            PropertyTypes.Boolean, PropertyTypes.SysTime, PropertyTypes.SysTime, PropertyTypes.SysTime,
            PropertyTypes.SysTime, PropertyTypes.Long, PropertyTypes.MultipleUnicode,
        ];

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 1);
        var (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertyIdsFromNames(0, names), handles, cancellationToken).ConfigureAwait(false);
        var ids = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
        if (ids.Count != names.Count || ids.Any(id => id == 0))
            throw new MapiFormatException("The store did not map every task named property.");

        uint Tag(int i) => ((uint)ids[i] << 16) | types[i];
        return new MapiTaskTags(Tag(0), Tag(1), Tag(2), Tag(3), Tag(4), Tag(5), Tag(6), Tag(7), Tag(8), Tag(9), Tag(10), Tag(11), Tag(12));
    }

    public static Task<ulong?> FindTasksFolderIdAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
        => MapiContactOperations.FindWellKnownFolderIdAsync(session, inboxFolderId, PropertyTags.IpmTaskEntryId, cancellationToken);

    private static uint[] Columns(MapiTaskTags tags) =>
    [
        PropertyTags.Mid, PropertyTags.MessageClass, PropertyTags.Subject, PropertyTags.Body,
        tags.TaskStartDate, tags.TaskDueDate, tags.TaskDateCompleted,
        PropertyTags.Importance, tags.TaskStatus, tags.PercentComplete, tags.TaskComplete,
        tags.ReminderSet, tags.ReminderTime, tags.Keywords,
    ];

    public static async Task<List<MapiTaskInfo>> ReadTasksAsync(MapiSession session, ulong folderId, MapiTaskTags tags, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var columns = Columns(tags);
        var rows = await ReadTableAsync(session, folderId, columns, 25, cancellationToken).ConfigureAwait(false);

        var tasks = new List<MapiTaskInfo>(rows.Count);
        var partialBodies = 0;
        foreach (var row in rows)
        {
            if (row[0].AsUInt64 is not { } mid)
                continue;

            var task = new MapiTaskInfo(
                mid,
                row[1].AsString ?? string.Empty,
                row[2].AsString,
                row[3].AsString,
                TaskDate(row[4].Value as DateTime?),
                TaskDate(row[5].Value as DateTime?),
                row[6].Value as DateTime?,
                row[7].AsUInt32,
                row[8].AsUInt32,
                row[9].Value as double?,
                row[10].AsBoolean,
                row[11].AsBoolean,
                row[12].Value as DateTime?,
                row[13].Value as string[]);

            // The table refuses or cuts a long body. Taking that as the note would replace the local note with a
            // partial or empty one, and the next write-back would push it to the server.
            if (task.IsTask && MapiMessageOperations.MayBeTruncatedStringCell(row[3]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                partialBodies++;
                var body = await MapiMessageOperations.ReadUnicodePropertyAsync(session, folderId, mid, PropertyTags.Body, cancellationToken).ConfigureAwait(false);
                task = task with { Body = body ?? task.Body };
            }

            tasks.Add(task);
        }

        diagnostics?.Invoke($"tasks 0x{folderId:X16}: {tasks.Count} rows ({tasks.Count(t => t.IsTask)} tasks, {tasks.Count(t => t.Completed)} complete, {partialBodies} bodies read off the message)");
        return tasks;
    }

    /// <summary>A task date as stored: the day itself, without time zone conversion; null when unset.</summary>
    private static DateTime? TaskDate(DateTime? value)
        => value is { } date && date < NoTaskDate ? date : null;

    private static async Task<List<PropertyValue[]>> ReadTableAsync(MapiSession session, ulong folderId, uint[] columns, ushort pageSize, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.DeferredErrors), handles, cancellationToken).ConfigureAwait(false);
            var rowCount = RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, columns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var result = new List<PropertyValue[]>((int)Math.Min(rowCount, 4096));
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, pageSize), handles, cancellationToken).ConfigureAwait(false);
                    var rows = RopFolder.ParseQueryRows(new RopReader(rops), columns);
                    if (rows.Count == 0)
                        break;

                    result.AddRange(rows);
                }

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
    /// Moves a task to another folder and returns its message id there, or null when it cannot be told apart. A move
    /// gives the message a new id; its search key travels with it, so the moved copy is found by that.
    /// </summary>
    public static async Task<ulong?> MoveTaskAsync(MapiSession session, ulong sourceFolderId, ulong messageId, ulong destinationFolderId, CancellationToken cancellationToken = default)
    {
        uint[] tags = [PropertyTags.SearchKey];
        var searchKey = (await ReadMessagePropertiesAsync(session, sourceFolderId, messageId, tags, cancellationToken).ConfigureAwait(false))[0].AsBinary;

        await MapiMessageOperations.MoveAsync(session, sourceFolderId, destinationFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (searchKey is not { Length: > 0 })
            return null;

        var rows = await ReadTableAsync(session, destinationFolderId, [PropertyTags.Mid, PropertyTags.SearchKey], 100, cancellationToken).ConfigureAwait(false);

        // A copy shares the search key; the moved message is the newest one, and message ids only grow.
        return rows
            .Where(row => row[1].AsBinary is { } key && key.AsSpan().SequenceEqual(searchKey))
            .Select(row => row[0].AsUInt64)
            .Where(mid => mid is not null)
            .Max();
    }

    private static async Task<PropertyValue[]> ReadMessagePropertiesAsync(MapiSession session, ulong folderId, ulong messageId, uint[] tags, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, tags), handles, cancellationToken).ConfigureAwait(false);
            return RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), tags);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates an IPM.Task in the folder; returns its message id.</summary>
    public static async Task<ulong> CreateTaskAsync(MapiSession session, ulong folderId, MapiTaskTags tags, MapiTaskWrite task, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessageWrite.BuildCreateMessage(0, 1, folderId), handles, cancellationToken).ConfigureAwait(false);
        var createdId = RopMessageWrite.ParseCreateMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            var savedId = await SetAndSaveAsync(session, handles, tags, task, null, includeClass: true, cancellationToken).ConfigureAwait(false);
            return savedId != 0 ? savedId : createdId ?? 0;
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task UpdateTaskAsync(MapiSession session, ulong folderId, ulong messageId, MapiTaskTags tags, MapiTaskWrite task, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 1, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            uint[] stateTags = [tags.TaskStartDate, PropertyTags.Importance, tags.TaskStatus];
            (rops, _) = await session.ExecuteAsync(RopProperties.BuildGetPropertiesSpecific(1, stateTags), handles, cancellationToken).ConfigureAwait(false);
            var values = RopProperties.ParseGetPropertiesSpecific(new RopReader(rops), stateTags);
            var existing = new MapiTaskState(TaskDate(values[0].AsDateTime), values[1].AsUInt32, values[2].AsUInt32);

            await SetAndSaveAsync(session, handles, tags, task, existing, includeClass: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ulong> SetAndSaveAsync(MapiSession session, uint[] handles, MapiTaskTags tags, MapiTaskWrite task, MapiTaskState? existing, bool includeClass, CancellationToken cancellationToken)
    {
        await MapiPropertyEdits.ApplyAsync(session, handles, 1, Properties(tags, task, includeClass, existing), "task", cancellationToken).ConfigureAwait(false);

        var (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(2, 1), handles, cancellationToken).ConfigureAwait(false);
        return RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
    }

    /// <summary>
    /// The task properties (MS-OXOTASK 2.2.2): status, complete flag and percent kept in step. The start and due dates
    /// are the days in the user's zone at midnight, stored without conversion (2.2.2.2.4, 2.2.2.2.5), with
    /// PidLidCommonStart and PidLidCommonEnd as their UTC equivalents (2.2.2.1.3); a start date needs a due date
    /// (2.2.2.2.4), so a task given only a start is due that day, and a start after the due date is not written. A
    /// reminder sets PidLidReminderSet, PidLidReminderTime and PidLidReminderSignalTime, with PidLidReminderDelta 0
    /// (MS-OXORMDR 2.2.1, 3.1.4.1.1). Categories are PidNameKeywords (MS-OXCMSG 2.2.1.17).
    /// <paramref name="existing"/> is the task's server state on an update: an in-progress status and a low importance
    /// the edit does not contradict are kept as the server has them.
    /// </summary>
    public static MapiPropertyEdits.Changes Properties(MapiTaskTags tags, MapiTaskWrite task, bool includeClass, MapiTaskState? existing = null)
    {
        var isUpdate = existing is not null;
        var changes = new MapiPropertyEdits.Changes();
        var values = changes.Set;
        if (includeClass)
            values.Add(TaggedPropertyValue.Unicode(PropertyTags.MessageClass, PropertyTags.TaskMessageClass));

        values.Add(TaggedPropertyValue.Unicode(PropertyTags.Subject, task.Subject ?? string.Empty));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.Body, task.Body ?? string.Empty));

        var importance = task.Importance == ImportanceNormal && existing?.Importance == ImportanceLow ? ImportanceLow : task.Importance;
        values.Add(TaggedPropertyValue.Long(PropertyTags.Importance, importance));

        if (task.IsComplete)
        {
            values.Add(TaggedPropertyValue.Long(tags.TaskStatus, MapiTaskInfo.StatusComplete));
            values.Add(TaggedPropertyValue.Boolean(tags.TaskComplete, true));
            values.Add(new TaggedPropertyValue(tags.PercentComplete, 1.0));
        }
        else if (existing?.Status is null or MapiTaskInfo.StatusComplete)
        {
            values.Add(TaggedPropertyValue.Long(tags.TaskStatus, MapiTaskInfo.StatusNotStarted));
            values.Add(TaggedPropertyValue.Boolean(tags.TaskComplete, false));
            values.Add(new TaggedPropertyValue(tags.PercentComplete, 0.0));
        }
        else
        {
            values.Add(TaggedPropertyValue.Boolean(tags.TaskComplete, false));
        }

        if (task.WritesStartDate)
            AddDates(changes, tags, task, isUpdate);
        else
            AddDueDateKeepingStart(changes, tags, task, existing);

        // PidLidTaskDateCompleted (2.2.2.2.9): midnight of the completion day in the user's zone, converted to UTC.
        if (task.IsComplete && task.CompletedDate is { } completed)
        {
            var completedUtc = completed.Kind == DateTimeKind.Local ? completed.ToUniversalTime() : DateTime.SpecifyKind(completed, DateTimeKind.Utc);
            values.Add(TaggedPropertyValue.SysTime(tags.TaskDateCompleted, LocalMidnightUtc(TimeZoneInfo.ConvertTimeFromUtc(completedUtc, TimeZoneInfo.Local).Date)));
        }

        if (task.WritesReminder)
            AddReminder(changes, tags, task);

        if (task.WritesCategories)
            MapiContactOperations.AddCategories(changes, tags.Keywords, task.Categories, deleteWhenEmpty: isUpdate);

        return changes;
    }

    /// <summary>The start and due days as the task has them; a task given only a start is due that day.</summary>
    private static void AddDates(MapiPropertyEdits.Changes changes, MapiTaskTags tags, MapiTaskWrite task, bool isUpdate)
    {
        var values = changes.Set;
        var start = task.StartDate?.Date;
        var due = task.DueDate?.Date ?? start;
        if (start is { } startCandidate && due is { } dueCandidate && startCandidate > dueCandidate)
            start = null;

        if (due is { } dueDay)
        {
            values.Add(TaggedPropertyValue.SysTime(tags.TaskDueDate, AsStoredDay(dueDay)));
            values.Add(TaggedPropertyValue.SysTime(tags.CommonEnd, LocalMidnightUtc(dueDay)));
        }
        else if (isUpdate)
        {
            changes.Delete.AddRange([tags.TaskDueDate, tags.CommonEnd]);
        }

        if (start is { } startDay)
        {
            values.Add(TaggedPropertyValue.SysTime(tags.TaskStartDate, AsStoredDay(startDay)));
            values.Add(TaggedPropertyValue.SysTime(tags.CommonStart, LocalMidnightUtc(startDay)));
        }
        else if (isUpdate)
        {
            changes.Delete.AddRange([tags.TaskStartDate, tags.CommonStart]);
        }
    }

    /// <summary>
    /// The due day when the start date stays the server's: the start is kept unless the task has none or it falls
    /// after the new due day, in which case it moves to the due day (2.2.2.2.5: the due date is not before the start).
    /// </summary>
    private static void AddDueDateKeepingStart(MapiPropertyEdits.Changes changes, MapiTaskTags tags, MapiTaskWrite task, MapiTaskState? existing)
    {
        if (task.DueDate?.Date is not { } due)
            return;

        changes.Set.Add(TaggedPropertyValue.SysTime(tags.TaskDueDate, AsStoredDay(due)));
        changes.Set.Add(TaggedPropertyValue.SysTime(tags.CommonEnd, LocalMidnightUtc(due)));

        if (existing?.StartDate is not { } start || start.Date > due)
        {
            changes.Set.Add(TaggedPropertyValue.SysTime(tags.TaskStartDate, AsStoredDay(due)));
            changes.Set.Add(TaggedPropertyValue.SysTime(tags.CommonStart, LocalMidnightUtc(due)));
        }
    }

    private static void AddReminder(MapiPropertyEdits.Changes changes, MapiTaskTags tags, MapiTaskWrite task)
    {
        var values = changes.Set;
        if (task.ReminderAtUtc is { } reminder)
        {
            var utc = reminder.Kind == DateTimeKind.Local ? reminder.ToUniversalTime() : DateTime.SpecifyKind(reminder, DateTimeKind.Utc);
            values.Add(TaggedPropertyValue.Boolean(tags.ReminderSet, true));
            values.Add(TaggedPropertyValue.SysTime(tags.ReminderTime, utc));
            values.Add(TaggedPropertyValue.SysTime(tags.ReminderSignalTime, utc));
            values.Add(TaggedPropertyValue.Long(tags.ReminderDelta, 0));
        }
        else
        {
            values.Add(TaggedPropertyValue.Boolean(tags.ReminderSet, false));
        }
    }

    /// <summary>The day as a task date stores it: midnight of that day, without time zone conversion.</summary>
    private static DateTime AsStoredDay(DateTime day) => new(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The UTC instant the day starts in the user's zone.</summary>
    private static DateTime LocalMidnightUtc(DateTime day)
    {
        var midnight = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        while (TimeZoneInfo.Local.IsInvalidTime(midnight))
            midnight = midnight.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, TimeZoneInfo.Local);
    }
}
