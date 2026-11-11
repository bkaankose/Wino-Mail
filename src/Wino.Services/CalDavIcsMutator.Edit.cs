#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

/// <summary>The VEVENT fields Wino edits; an edit names the ones it changed.</summary>
public static class CalDavEventFields
{
    public const string Summary = "SUMMARY";
    public const string Description = "DESCRIPTION";
    public const string Location = "LOCATION";
    public const string Status = "STATUS";
    public const string Transparency = "TRANSP";
    public const string Classification = "CLASS";
    public const string Start = "DTSTART";
    public const string End = "DTEND";
    public const string Organizer = "ORGANIZER";
    public const string Attendees = "ATTENDEE";
    public const string Recurrence = "RRULE";

    /// <summary>The properties each field is written as.</summary>
    internal static string[] PropertiesOf(string field)
        => field switch
        {
            Description => ["DESCRIPTION", CalDavEventDescription.AlternateDescriptionProperty],
            End => ["DTEND", "DURATION"],
            Recurrence => ["RRULE", "RDATE", "EXDATE", "EXRULE"],
            _ => [field]
        };

    /// <summary>
    /// Changes the organizer's client marks with a new SEQUENCE (RFC 5545 section 3.8.7.4,
    /// RFC 5546 section 2.1.4): the time, the recurrence and the status of the event.
    /// </summary>
    internal static readonly IReadOnlySet<string> Significant = new HashSet<string>(StringComparer.Ordinal) { Start, End, Recurrence, Status };
}

/// <summary>The result of applying the user's edit to a stored resource.</summary>
public sealed class CalDavEventEdit
{
    public required string IcsContent { get; init; }

    /// <summary>The <see cref="CalDavEventFields"/> the edit changed; empty when the resource already matched.</summary>
    public required IReadOnlyCollection<string> ChangedFields { get; init; }

    public bool HasChanges => ChangedFields.Count > 0;
}

public static partial class CalDavIcsMutator
{
    /// <summary>
    /// Applies the user's edit to the stored resource line by line. Only the properties whose value
    /// differs from the stored event are rewritten; every other line (attendees Wino does not model,
    /// parameters, X- properties, VALARM, DURATION, VTIMEZONE, other components) is kept as the
    /// server stored it. Attendees are matched by address: unchanged ones keep their line, removed
    /// ones are dropped, new ones are added, and ATTENDEE values that are not mail addresses
    /// (RFC 5545 section 3.3.3 allows any URI) always stay. SEQUENCE is incremented only for a
    /// significant change made by the organizer (RFC 5545 section 3.8.7.4). With
    /// <paramref name="onlyFields"/>, fields outside that set are left as stored.
    /// </summary>
    public static CalDavEventEdit EditEvent(
        string existingIcs,
        CalendarItem item,
        IReadOnlyCollection<CalendarEventAttendee>? attendees,
        CalDavUserIdentity? identity,
        IReadOnlyCollection<string>? onlyFields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingIcs);
        ArgumentNullException.ThrowIfNull(item);

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var originalEvents = calendar.Events.ToList();
        var (uid, occurrenceKey) = SplitRemoteEventId(item.RemoteEventId);
        var target = FindEvent(calendar, uid, occurrenceKey);
        CalendarEvent? master = null;

        if (target == null && !string.IsNullOrWhiteSpace(occurrenceKey))
        {
            master = FindMaster(calendar, uid)
                ?? throw new InvalidOperationException("The recurring CalDAV master event is missing from the stored resource.");

            target = CreateOccurrenceException(master, occurrenceKey);
            calendar.Events.Add(target);
        }

        if (target == null)
            throw new InvalidOperationException("The CalDAV event is missing from the stored resource.");

        var isNewException = master != null;
        var lines = CalDavIcsLines.Parse(existingIcs);
        var components = CalDavIcsLines.TopLevelComponents(lines);
        var eventBlocks = components.Where(block => block.Name == "VEVENT").ToList();
        var baselineIndex = IndexOfReference(originalEvents, isNewException ? master! : target);

        if (eventBlocks.Count != originalEvents.Count || baselineIndex < 0 ||
            !string.Equals(eventBlocks[baselineIndex].Properties(lines).FirstOrDefault(line => line.Name == "UID")?.Value.Trim(), uid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored CalDAV resource could not be matched to its events.");
        }

        var baselineBlock = eventBlocks[baselineIndex];
        var baseline = baselineBlock.Properties(lines).ToList();
        var changes = new Dictionary<string, List<IcsOutputLine>?>(StringComparer.OrdinalIgnoreCase);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var newTimeZones = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        bool Allowed(string field) => onlyFields == null || onlyFields.Contains(field);

        void Set(string field, string property, string? line)
        {
            changes[property] = line == null ? null : [IcsOutputLine.New(line)];
            fields.Add(field);
        }

        if (Allowed(CalDavEventFields.Summary) && !string.Equals(target.Summary ?? string.Empty, item.Title ?? string.Empty, StringComparison.Ordinal))
            Set(CalDavEventFields.Summary, "SUMMARY", $"SUMMARY:{CalDavIcsLines.EscapeText(item.Title)}");

        if (Allowed(CalDavEventFields.Description) &&
            !string.Equals(NormalizeNotes(CalDavEventDescription.Read(target)), NormalizeNotes(item.Description), StringComparison.Ordinal))
        {
            var (text, html) = CalDavEventDescription.Split(item.Description);
            Set(CalDavEventFields.Description, "DESCRIPTION", text == null ? null : $"DESCRIPTION:{CalDavIcsLines.EscapeText(text)}");
            Set(CalDavEventFields.Description, CalDavEventDescription.AlternateDescriptionProperty,
                html == null ? null : $"{CalDavEventDescription.AlternateDescriptionProperty};FMTTYPE=text/html:{CalDavIcsLines.EscapeText(html)}");
        }

        if (Allowed(CalDavEventFields.Location) && !string.Equals(target.Location ?? string.Empty, item.Location ?? string.Empty, StringComparison.Ordinal))
            Set(CalDavEventFields.Location, "LOCATION", string.IsNullOrWhiteSpace(item.Location) ? null : $"LOCATION:{CalDavIcsLines.EscapeText(item.Location)}");

        // Organizer first: whether the user organizes decides STATUS and SEQUENCE.
        var hasAttendees = attendees?.Any(value => value != null && !string.IsNullOrWhiteSpace(value.Email)) == true;
        var storedOrganizer = baseline.FirstOrDefault(line => line.Name == "ORGANIZER");
        var organizerValue = storedOrganizer?.Value;
        if (Allowed(CalDavEventFields.Organizer) && TryResolveOrganizer(storedOrganizer, item, identity, hasAttendees, out var organizerAddress, out var organizerLine))
        {
            Set(CalDavEventFields.Organizer, "ORGANIZER", organizerLine);
            organizerValue = organizerAddress;
        }

        var organizedByOther = identity != null && !string.IsNullOrWhiteSpace(organizerValue) && !identity.IsUser(organizerValue);

        // Only the organizer sets STATUS (RFC 5546 section 2.1.4); an attendee's answer is its PARTSTAT.
        var desiredStatus = MapStatus(item.Status);
        if (Allowed(CalDavEventFields.Status) && !organizedByOther && !string.Equals(EffectiveStatus(target.Status), desiredStatus, StringComparison.Ordinal))
            Set(CalDavEventFields.Status, "STATUS", $"STATUS:{desiredStatus}");

        // TRANSP defaults to OPAQUE (RFC 5545 section 3.8.2.7).
        var desiredTransparency = item.ShowAs == CalendarItemShowAs.Free ? "TRANSPARENT" : "OPAQUE";
        var storedTransparency = string.Equals(target.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? "TRANSPARENT" : "OPAQUE";
        if (Allowed(CalDavEventFields.Transparency) && !string.Equals(storedTransparency, desiredTransparency, StringComparison.Ordinal))
            Set(CalDavEventFields.Transparency, "TRANSP", $"TRANSP:{desiredTransparency}");

        if (Allowed(CalDavEventFields.Classification) && ReadVisibility(target.Class) != item.Visibility)
        {
            var classification = MapVisibility(item.Visibility);
            Set(CalDavEventFields.Classification, "CLASS", classification == null ? null : $"CLASS:{classification}");
        }

        ApplyTimeChanges(target, item, baseline, Allowed, Set, newTimeZones);

        if (Allowed(CalDavEventFields.Attendees))
        {
            var (attendeeLines, attendeesChanged) = MergeAttendeeLines(baseline.Where(line => line.Name == "ATTENDEE"), attendees);
            if (attendeesChanged || isNewException)
                changes["ATTENDEE"] = attendeeLines;

            if (attendeesChanged)
                fields.Add(CalDavEventFields.Attendees);
        }

        var recurrenceChanged = false;
        if (Allowed(CalDavEventFields.Recurrence) && target.RecurrenceIdentifier == null &&
            !string.Equals(BuildNormalizedRecurrence(target), item.Recurrence ?? string.Empty, StringComparison.Ordinal))
        {
            ApplyRecurrenceProperties(target, item.Recurrence ?? string.Empty);
            recurrenceChanged = true;
            fields.Add(CalDavEventFields.Recurrence);
        }

        if (fields.Count == 0)
            return new CalDavEventEdit { IcsContent = existingIcs, ChangedFields = Array.Empty<string>() };

        var stamp = CalDavIcsLines.FormatUtc(DateTime.UtcNow);
        changes["DTSTAMP"] = [IcsOutputLine.New($"DTSTAMP:{stamp}")];
        changes["LAST-MODIFIED"] = [IcsOutputLine.New($"LAST-MODIFIED:{stamp}")];
        if (!organizedByOther && fields.Overlaps(CalDavEventFields.Significant))
            changes["SEQUENCE"] = [IcsOutputLine.New($"SEQUENCE:{target.Sequence + 1}")];

        List<string>? newBlock = null;
        if (recurrenceChanged || isNewException)
        {
            if (isNewException && !changes.ContainsKey("ORGANIZER"))
                changes["ORGANIZER"] = storedOrganizer == null ? null : [IcsOutputLine.Keep(storedOrganizer)];

            newBlock = ApplySerializedProperties(calendar, target, changes, recurrenceChanged, isNewException);
        }

        var timeZoneLines = BuildMissingTimeZones(lines, components, newTimeZones);
        var output = new List<string>(lines.Count + 16);
        var calendarEnd = CalDavIcsLines.CalendarEndIndex(lines);
        var firstEvent = eventBlocks.Count > 0 ? eventBlocks[0].BeginIndex : calendarEnd;

        for (var index = 0; index < lines.Count; index++)
        {
            if (index == firstEvent)
                output.AddRange(timeZoneLines);

            if (index == calendarEnd && newBlock != null)
                output.AddRange(newBlock);

            if (!isNewException && index == baselineBlock.BeginIndex)
            {
                output.AddRange(CalDavIcsLines.PatchBlock(lines, baselineBlock, changes));
                index = baselineBlock.EndIndex;
                continue;
            }

            output.Add(lines[index].Raw);
        }

        return new CalDavEventEdit { IcsContent = CalDavIcsLines.Join(output), ChangedFields = fields };
    }

    /// <summary>
    /// The stored copy of the properties behind <paramref name="field"/> on the event
    /// <paramref name="remoteEventId"/> names (or its master when the occurrence has no exception),
    /// unfolded, sorted, and without SCHEDULE-STATUS, which a scheduling server rewrites on its own
    /// (RFC 6638 section 3.2.9). Used to tell whether another client changed the same field.
    /// </summary>
    public static IReadOnlyList<string> ReadFieldLines(string icsContent, string remoteEventId, string field)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return Array.Empty<string>();

        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
        var calendar = IcalCalendar.Load(icsContent);
        if (calendar == null)
            return Array.Empty<string>();

        var events = calendar.Events.ToList();
        var target = FindEvent(calendar, uid, occurrenceKey) ?? FindMaster(calendar, uid);
        var index = target == null ? -1 : IndexOfReference(events, target);
        var lines = CalDavIcsLines.Parse(icsContent);
        var blocks = CalDavIcsLines.TopLevelComponents(lines).Where(block => block.Name == "VEVENT").ToList();
        if (index < 0 || blocks.Count != events.Count)
            return Array.Empty<string>();

        var names = CalDavEventFields.PropertiesOf(field);
        return blocks[index].Properties(lines)
            .Where(line => names.Contains(line.Name, StringComparer.OrdinalIgnoreCase))
            .Select(line => line.WithParameters(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["SCHEDULE-STATUS"] = null }))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();
    }

    private static void ApplyTimeChanges(
        CalendarEvent target,
        CalendarItem item,
        IReadOnlyList<IcsLine> baseline,
        Func<string, bool> allowed,
        Action<string, string, string?> set,
        Dictionary<string, DateTime> newTimeZones)
    {
        var storedStart = target.Start;
        if (storedStart == null)
            return;

        CalDateTime desiredStart;
        CalDateTime? desiredEnd;
        if (item.IsAllDayEvent)
        {
            desiredStart = new CalDateTime(DateOnly.FromDateTime(item.StartDate));
            desiredEnd = new CalDateTime(DateOnly.FromDateTime(item.EndDate));
        }
        else
        {
            desiredStart = CreateCalDateTime(item.StartDate, item.StartTimeZone, storedStart);
            desiredEnd = item.DurationInSeconds > 0
                ? CreateCalDateTime(item.EndDate, string.IsNullOrEmpty(item.EndTimeZone) ? item.StartTimeZone : item.EndTimeZone, target.End ?? storedStart)
                : null;
        }

        if (allowed(CalDavEventFields.Start) && !SameDateTime(storedStart, desiredStart))
        {
            set(CalDavEventFields.Start, "DTSTART", FormatDateTimeProperty("DTSTART", desiredStart));
            NoteTimeZone(desiredStart, newTimeZones);
        }

        if (!allowed(CalDavEventFields.End))
            return;

        var desiredAllDay = !desiredStart.HasTime;
        var durationLine = baseline.FirstOrDefault(line => line.Name == "DURATION");
        var hasEnd = baseline.Any(line => line.Name == "DTEND");

        if (durationLine != null && !hasEnd)
        {
            // DTSTART with DURATION stays that way (RFC 5545 section 3.6.1); only the length is rewritten.
            var storedLength = target.Duration?.ToTimeSpanUnspecified();
            var desiredLength = desiredEnd == null
                ? TimeSpan.Zero
                : desiredAllDay ? TimeSpan.FromDays((item.EndDate.Date - item.StartDate.Date).Days) : TimeSpan.FromSeconds(item.DurationInSeconds);
            var needsDayForm = desiredAllDay && durationLine.Value.Contains('T', StringComparison.OrdinalIgnoreCase);

            if (storedLength != desiredLength || needsDayForm)
                set(CalDavEventFields.End, "DURATION", $"DURATION:{FormatDuration(desiredLength, desiredAllDay)}");

            return;
        }

        if (!hasEnd)
        {
            // Without DTEND an all-day event lasts one day and a timed one has no length.
            var storedAllDay = !storedStart.HasTime;
            var storedLength = storedAllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            var desiredLength = desiredEnd == null
                ? TimeSpan.Zero
                : desiredAllDay ? TimeSpan.FromDays((item.EndDate.Date - item.StartDate.Date).Days) : TimeSpan.FromSeconds(item.DurationInSeconds);

            if (storedAllDay == desiredAllDay && storedLength == desiredLength)
                return;

            if (desiredEnd != null)
            {
                set(CalDavEventFields.End, "DTEND", FormatDateTimeProperty("DTEND", desiredEnd));
                NoteTimeZone(desiredEnd, newTimeZones);
            }

            return;
        }

        var effectiveEnd = desiredEnd ?? desiredStart;
        if (!SameDateTime(target.End, effectiveEnd))
        {
            set(CalDavEventFields.End, "DTEND", FormatDateTimeProperty("DTEND", effectiveEnd));
            NoteTimeZone(effectiveEnd, newTimeZones);
        }
    }

    /// <summary>
    /// Decides the ORGANIZER line. Returns false when the stored one stays: the same address, the
    /// user under another address of the set, or no organizer address known to Wino (a stored
    /// ORGANIZER that is not a mail address is never replaced).
    /// </summary>
    private static bool TryResolveOrganizer(IcsLine? stored, CalendarItem item, CalDavUserIdentity? identity, bool hasAttendees, out string? address, out string? line)
    {
        address = null;
        line = null;
        var desired = item.OrganizerEmail?.Trim() ?? string.Empty;

        if (stored != null)
        {
            if (desired.Length == 0)
                return false;

            if (string.Equals(CalDavUserIdentity.ExtractEmail(stored.Value), desired, StringComparison.OrdinalIgnoreCase))
                return false;

            if (identity != null && identity.IsUser(stored.Value) && identity.IsUser(desired))
                return false;
        }

        if (identity != null && ((desired.Length > 0 && identity.IsUser(desired)) || (desired.Length == 0 && hasAttendees)))
            desired = identity.ResolveOrganizerAddress(desired);

        if (desired.Length == 0)
            return false;

        var commonName = string.IsNullOrWhiteSpace(item.OrganizerDisplayName) ? desired : item.OrganizerDisplayName.Trim();
        address = $"mailto:{desired}";
        line = $"ORGANIZER;CN={CalDavIcsLines.ParameterValue(commonName)}:mailto:{desired}";
        return true;
    }

    /// <summary>
    /// The ATTENDEE lines after the edit. A stored line whose address Wino models (a mail address)
    /// stays as written unless the user removed that attendee or changed its role or name; lines
    /// with other calendar user addresses always stay. New attendees are appended.
    /// </summary>
    private static (List<IcsOutputLine> Lines, bool Changed) MergeAttendeeLines(IEnumerable<IcsLine> stored, IReadOnlyCollection<CalendarEventAttendee>? attendees)
    {
        var wanted = attendees?.Where(value => value != null && !string.IsNullOrWhiteSpace(value.Email)).ToList() ?? [];
        var matched = new HashSet<CalendarEventAttendee>(ReferenceEqualityComparer.Instance);
        var result = new List<IcsOutputLine>();
        var changed = false;

        foreach (var line in stored)
        {
            var email = CalDavUserIdentity.ExtractEmail(line.Value);
            if (email.Length == 0)
            {
                result.Add(IcsOutputLine.Keep(line));
                continue;
            }

            var match = wanted.FirstOrDefault(value => !matched.Contains(value) && string.Equals(value.Email.Trim(), email, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                // A second line for an address that is still wanted is not Wino's to remove.
                if (wanted.Any(value => string.Equals(value.Email.Trim(), email, StringComparison.OrdinalIgnoreCase)))
                    result.Add(IcsOutputLine.Keep(line));
                else
                    changed = true;

                continue;
            }

            matched.Add(match);
            var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var role = line.GetParameter("ROLE");
            var storedOptional = string.Equals(role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase);
            if (storedOptional != match.IsOptionalAttendee && !string.Equals(role, "CHAIR", StringComparison.OrdinalIgnoreCase))
                parameters["ROLE"] = match.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";

            var name = match.Name?.Trim();
            var storedName = line.GetParameter("CN")?.Trim();
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.Equals(name, match.Email.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, storedName, StringComparison.Ordinal))
            {
                parameters["CN"] = name;
            }

            if (parameters.Count == 0)
            {
                result.Add(IcsOutputLine.Keep(line));
                continue;
            }

            result.Add(IcsOutputLine.New(line.WithParameters(parameters)));
            changed = true;
        }

        foreach (var value in wanted.Where(value => !matched.Contains(value)).DistinctBy(value => value.Email.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var email = value.Email.Trim();
            if (matched.Any(other => string.Equals(other.Email.Trim(), email, StringComparison.OrdinalIgnoreCase)))
                continue;

            var name = string.IsNullOrWhiteSpace(value.Name) ? email : value.Name.Trim();
            result.Add(IcsOutputLine.New(
                $"ATTENDEE;CN={CalDavIcsLines.ParameterValue(name)};ROLE={(value.IsOptionalAttendee ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT")};PARTSTAT={MapParticipationStatus(value.AttendenceStatus)}:mailto:{email}"));
            changed = true;
        }

        return (result, changed);
    }

    /// <summary>
    /// Serializes the Ical.Net calendar to take the properties only Ical.Net builds (the recurrence
    /// lines) for the target, and the whole component of a new occurrence exception. ATTENDEE and
    /// ORGANIZER never come from it: Ical.Net 5 drops urn:uuid values and writes other URIs as
    /// mailto:, so those lines come from the stored text.
    /// </summary>
    private static List<string>? ApplySerializedProperties(
        IcalCalendar calendar,
        CalendarEvent target,
        Dictionary<string, List<IcsOutputLine>?> changes,
        bool recurrenceChanged,
        bool isNewException)
    {
        foreach (var calendarEvent in calendar.Events)
        {
            calendarEvent.Properties.Clear("ATTENDEE");
            calendarEvent.Properties.Clear("ORGANIZER");
        }

        var serialized = new CalendarSerializer(calendar).SerializeToString()
            ?? throw new InvalidOperationException("The edited CalDAV event could not be serialized.");
        var lines = CalDavIcsLines.Parse(serialized);
        var blocks = CalDavIcsLines.TopLevelComponents(lines).Where(block => block.Name == "VEVENT").ToList();
        var index = IndexOfReference(calendar.Events.ToList(), target);
        if (index < 0 || index >= blocks.Count)
            throw new InvalidOperationException("The edited CalDAV event could not be serialized.");

        var block = blocks[index];
        if (recurrenceChanged)
        {
            foreach (var name in CalDavEventFields.PropertiesOf(CalDavEventFields.Recurrence))
            {
                var values = block.Properties(lines).Where(line => line.Name == name).Select(IcsOutputLine.Keep).ToList();
                changes[name] = values.Count == 0 ? null : values;
            }
        }

        return isNewException ? CalDavIcsLines.PatchBlock(lines, block, changes).ToList() : null;
    }

    /// <summary>VTIMEZONE components (RFC 5545 section 3.6.5) for the TZIDs the edit introduced that the resource does not define.</summary>
    private static List<string> BuildMissingTimeZones(IReadOnlyList<IcsLine> lines, IReadOnlyList<IcsBlock> components, IReadOnlyDictionary<string, DateTime> newTimeZones)
    {
        var result = new List<string>();
        if (newTimeZones.Count == 0)
            return result;

        var defined = components
            .Where(block => block.Name == "VTIMEZONE")
            .Select(block => block.Properties(lines).FirstOrDefault(line => line.Name == "TZID")?.Value.Trim())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (tzId, earliest) in newTimeZones)
        {
            if (defined.Contains(tzId))
                continue;

            try
            {
                var zoneCalendar = new IcalCalendar();
                zoneCalendar.AddTimeZone(tzId, earliest, includeHistoricalData: false);
                var zoneLines = CalDavIcsLines.Parse(new CalendarSerializer(zoneCalendar).SerializeToString() ?? string.Empty);
                foreach (var block in CalDavIcsLines.TopLevelComponents(zoneLines).Where(block => block.Name == "VTIMEZONE"))
                {
                    for (var index = block.BeginIndex; index <= block.EndIndex; index++)
                        result.Add(zoneLines[index].Raw);
                }
            }
            catch (Exception ex)
            {
                // A TZID without a definition is still readable by most servers; the event is written anyway.
                Serilog.Log.ForContext(typeof(CalDavIcsMutator)).Warning(ex, "Could not build a VTIMEZONE for {TzId}", tzId);
            }
        }

        return result;
    }

    private static void NoteTimeZone(CalDateTime value, Dictionary<string, DateTime> newTimeZones)
    {
        var tzId = value.TzId;
        if (!value.HasTime || string.IsNullOrWhiteSpace(tzId) || CalDavTimeZones.IsUtc(tzId))
            return;

        if (!newTimeZones.TryGetValue(tzId, out var earliest) || value.Value < earliest)
            newTimeZones[tzId] = value.Value;
    }

    /// <summary>Same value type, same zone and same wall-clock time; unknown zones compare by name.</summary>
    private static bool SameDateTime(CalDateTime? left, CalDateTime? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        if (left.HasTime != right.HasTime)
            return false;

        if (!left.HasTime)
            return left.Value.Date == right.Value.Date;

        if (left.IsFloating || right.IsFloating)
            return left.IsFloating && right.IsFloating && left.Value == right.Value;

        var leftUtc = CalDavTimeZones.IsUtc(left.TzId);
        var rightUtc = CalDavTimeZones.IsUtc(right.TzId);
        if (leftUtc != rightUtc)
            return false;

        if (!leftUtc && !string.Equals(left.TzId, right.TzId, StringComparison.OrdinalIgnoreCase) && !CalDavTimeZones.IsSameZone(left.TzId, right.TzId))
            return false;

        return left.Value == right.Value;
    }

    private static string FormatDateTimeProperty(string name, CalDateTime value)
    {
        if (!value.HasTime)
            return $"{name};VALUE=DATE:{value.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

        var local = value.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (value.IsFloating)
            return $"{name}:{local}";

        return CalDavTimeZones.IsUtc(value.TzId)
            ? $"{name}:{local}Z"
            : $"{name};TZID={CalDavIcsLines.ParameterValue(value.TzId!)}:{local}";
    }

    /// <summary>RFC 5545 section 3.3.6; a DATE event's length is written in days.</summary>
    private static string FormatDuration(TimeSpan length, bool inDays)
    {
        if (length < TimeSpan.Zero)
            length = TimeSpan.Zero;

        if (inDays)
            return $"P{Math.Max(0, (int)Math.Round(length.TotalDays))}D";

        if (length == TimeSpan.Zero)
            return "PT0S";

        var text = "P";
        if (length.Days > 0)
            text += $"{length.Days}D";

        if (length.Hours > 0 || length.Minutes > 0 || length.Seconds > 0)
        {
            text += "T";
            if (length.Hours > 0)
                text += $"{length.Hours}H";
            if (length.Minutes > 0)
                text += $"{length.Minutes}M";
            if (length.Seconds > 0)
                text += $"{length.Seconds}S";
        }

        return text;
    }

    private static string EffectiveStatus(string? status)
        => status?.Trim().ToUpperInvariant() switch
        {
            "CANCELLED" => "CANCELLED",
            "TENTATIVE" => "TENTATIVE",
            _ => "CONFIRMED"
        };

    /// <summary>The visibility an imported CLASS maps to (CalDavClient.MapVisibility); unknown values are the default.</summary>
    private static CalendarItemVisibility ReadVisibility(string? classValue)
        => classValue?.Trim().ToUpperInvariant() switch
        {
            "PUBLIC" => CalendarItemVisibility.Public,
            "PRIVATE" => CalendarItemVisibility.Private,
            "CONFIDENTIAL" => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Default
        };

    private static string NormalizeNotes(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static int IndexOfReference(IReadOnlyList<CalendarEvent> events, CalendarEvent target)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (ReferenceEquals(events[index], target))
                return index;
        }

        return -1;
    }
}
