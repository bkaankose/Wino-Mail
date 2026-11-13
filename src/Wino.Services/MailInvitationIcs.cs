#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

/// <summary>
/// The iCalendar objects Wino writes when it answers an iMIP invitation itself: the attendee's
/// stored copy and the REPLY sent to the organizer. Both are made from the lines of the received
/// object, so every property Wino does not change stays as the organizer wrote it.
/// </summary>
public static class MailInvitationIcs
{
    public const string ProductId = "-//Wino Mail//Calendar//EN";

    /// <summary>Event properties that describe the user's own copy rather than the organizer's event.</summary>
    private static readonly HashSet<string> UserOwnedProperties = new(StringComparer.OrdinalIgnoreCase) { "CATEGORIES", "TRANSP", "COLOR" };

    /// <summary>
    /// The calendar object to store for an invitation (RFC 6638 section 7.1, RFC 4791 section 4.1):
    /// no METHOD, only the invitation's events and time zones, ORGANIZER with SCHEDULE-AGENT=CLIENT
    /// because Wino sends the reply itself, and the user's PARTSTAT. A user missing from every event
    /// (a forwarded invitation) is added as an attendee.
    /// </summary>
    public static string CreateAttendeeCopy(
        string invitationIcs,
        string uid,
        CalDavUserIdentity identity,
        string attendeeAddress,
        string? attendeeName,
        AttendeeStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationIcs);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentNullException.ThrowIfNull(identity);

        var lines = CalDavIcsLines.Parse(invitationIcs);
        var blocks = CalDavIcsLines.TopLevelComponents(lines);
        var events = blocks.Where(block => IsEventOf(block, lines, uid)).ToList();
        if (events.Count == 0)
            throw new InvalidOperationException("The invitation has no event with its UID.");

        var userIsListed = events.Any(block => block.Properties(lines).Any(line => line.Name == "ATTENDEE" && identity.IsUser(line.Value)));
        var partStat = FormatParticipationStatus(status);
        var byBegin = blocks.ToDictionary(block => block.BeginIndex);
        var output = new List<string>(lines.Count + 4);

        for (var index = 0; index < lines.Count; index++)
        {
            if (byBegin.TryGetValue(index, out var block))
            {
                if (block.Name == "VTIMEZONE")
                {
                    output.AddRange(RawLines(lines, block));
                }
                else if (events.Contains(block))
                {
                    var changes = OrganizerChanges(lines, block);
                    var attendees = new List<IcsOutputLine>();

                    foreach (var line in block.Properties(lines).Where(line => line.Name == "ATTENDEE"))
                    {
                        attendees.Add(identity.IsUser(line.Value)
                            ? IcsOutputLine.New(line.WithParameters(Parameters(("PARTSTAT", partStat), ("SCHEDULE-STATUS", null))))
                            : IcsOutputLine.Keep(line));
                    }

                    if (!userIsListed)
                        attendees.Add(IcsOutputLine.New(CreateAttendeeLine(attendeeAddress, attendeeName, partStat)));

                    changes["ATTENDEE"] = attendees;
                    output.AddRange(CalDavIcsLines.PatchBlock(lines, block, changes));
                }

                index = block.EndIndex;
                continue;
            }

            if (lines[index].Name == "METHOD")
                continue;

            output.Add(lines[index].Raw);
        }

        return CalDavIcsLines.Join(output);
    }

    /// <summary>
    /// The attendee's copy after answering an invitation, built from the stored object when the
    /// calendar has one. Each component the invitation carries (the series or one override, keyed by
    /// RECURRENCE-ID) replaces the stored one when its SEQUENCE is higher, or is added when the
    /// calendar has none for that RECURRENCE-ID; otherwise the stored one stays. A replaced or added
    /// component keeps the user's own data from the stored one (or, for a new override, from the
    /// stored series): alarms, CATEGORIES, TRANSP, COLOR and X- properties the invitation lacks.
    /// Stored overrides the invitation does not carry stay. Then the user's PARTSTAT is set (adding
    /// <paramref name="attendeeAddress"/> when the event does not list the user) and ORGANIZER gets
    /// SCHEDULE-AGENT=CLIENT.
    /// </summary>
    /// <param name="recurrenceId">The invitation's occurrence key, empty when it is for the whole event.</param>
    public static string CreateAnsweredCopy(
        string? storedIcs,
        string invitationIcs,
        string uid,
        string recurrenceId,
        CalDavUserIdentity identity,
        string attendeeAddress,
        string? attendeeName,
        AttendeeStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attendeeAddress);

        var copy = CreateAttendeeCopy(invitationIcs, uid, identity, attendeeAddress, attendeeName, status);
        if (string.IsNullOrWhiteSpace(storedIcs))
            return copy;

        var merged = MergeComponents(storedIcs, copy, uid);
        var remoteEventId = string.IsNullOrEmpty(recurrenceId) ? uid : $"{uid}::{recurrenceId}";
        var answered = CalDavIcsMutator.SetOwnParticipation(merged, remoteEventId, identity, status, attendeeAddress);
        return SetClientScheduling(answered, uid);
    }

    /// <summary>
    /// Marks the events of <paramref name="uid"/> as scheduled by the client (SCHEDULE-AGENT=CLIENT on
    /// ORGANIZER, RFC 6638 section 7.1) so the server sends no reply of its own when the user's
    /// PARTSTAT changes. Every other line stays as stored.
    /// </summary>
    public static string SetClientScheduling(string icsContent, string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(icsContent);

        var lines = CalDavIcsLines.Parse(icsContent);
        var byBegin = CalDavIcsLines.TopLevelComponents(lines)
            .Where(block => IsEventOf(block, lines, uid))
            .ToDictionary(block => block.BeginIndex);
        var output = new List<string>(lines.Count);

        for (var index = 0; index < lines.Count; index++)
        {
            if (byBegin.TryGetValue(index, out var block))
            {
                output.AddRange(CalDavIcsLines.PatchBlock(lines, block, OrganizerChanges(lines, block)));
                index = block.EndIndex;
                continue;
            }

            output.Add(lines[index].Raw);
        }

        return CalDavIcsLines.Join(output);
    }

    /// <summary>
    /// An iTIP REPLY (RFC 5546 section 3.2.3) to the invitation: the event's UID, RECURRENCE-ID,
    /// SEQUENCE, ORGANIZER, times and SUMMARY as received, the time zones they use, a new DTSTAMP,
    /// exactly one ATTENDEE (the user, with the answer) and an optional COMMENT.
    /// </summary>
    public static string CreateReply(
        string invitationIcs,
        string uid,
        CalDavUserIdentity identity,
        string attendeeAddress,
        string? attendeeName,
        AttendeeStatus status,
        string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationIcs);
        ArgumentNullException.ThrowIfNull(identity);

        var lines = CalDavIcsLines.Parse(invitationIcs);
        var blocks = CalDavIcsLines.TopLevelComponents(lines);
        var events = blocks.Where(block => IsEventOf(block, lines, uid)).ToList();
        var target = events.FirstOrDefault(block => !block.Properties(lines).Any(line => line.Name == "RECURRENCE-ID"))
                     ?? events.FirstOrDefault()
                     ?? throw new InvalidOperationException("The invitation has no event with its UID.");

        var properties = target.Properties(lines).ToList();
        var partStat = FormatParticipationStatus(status);
        var copied = new[] { "UID", "RECURRENCE-ID", "SEQUENCE", "ORGANIZER", "DTSTART", "DTEND", "DURATION", "SUMMARY" };

        var eventLines = new List<string>();
        foreach (var name in copied)
        {
            var line = properties.FirstOrDefault(value => value.Name == name);
            if (line != null)
                eventLines.Add(line.Raw);
        }

        eventLines.Add($"DTSTAMP:{CalDavIcsLines.FormatUtc(DateTime.UtcNow)}");

        var own = properties.FirstOrDefault(line => line.Name == "ATTENDEE" && identity.IsUser(line.Value));
        eventLines.Add(CalDavIcsLines.Fold(own != null
            ? own.WithParameters(Parameters(("PARTSTAT", partStat), ("RSVP", null), ("SCHEDULE-STATUS", null), ("SCHEDULE-AGENT", null)))
            : CreateAttendeeLine(attendeeAddress, attendeeName, partStat)));

        if (!string.IsNullOrWhiteSpace(comment))
            eventLines.Add(CalDavIcsLines.Fold($"COMMENT:{CalDavIcsLines.EscapeText(comment.Trim())}"));

        var timeZoneIds = properties
            .Where(line => line.Name is "DTSTART" or "DTEND" or "RECURRENCE-ID")
            .Select(line => line.GetParameter("TZID"))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var output = new List<string>
        {
            "BEGIN:VCALENDAR",
            $"PRODID:{ProductId}",
            "VERSION:2.0",
            "CALSCALE:GREGORIAN",
            "METHOD:REPLY"
        };

        foreach (var zone in blocks.Where(block => block.Name == "VTIMEZONE"))
        {
            var tzId = zone.Properties(lines).FirstOrDefault(line => line.Name == "TZID")?.Value.Trim();
            if (tzId != null && timeZoneIds.Contains(tzId))
                output.AddRange(RawLines(lines, zone));
        }

        output.Add("BEGIN:VEVENT");
        output.AddRange(eventLines);
        output.Add("END:VEVENT");
        output.Add("END:VCALENDAR");

        return CalDavIcsLines.Join(output);
    }

    public static string FormatParticipationStatus(AttendeeStatus status)
        => status switch
        {
            AttendeeStatus.Accepted => "ACCEPTED",
            AttendeeStatus.Tentative => "TENTATIVE",
            AttendeeStatus.Declined => "DECLINED",
            _ => "NEEDS-ACTION"
        };

    private static string MergeComponents(string storedIcs, string copyIcs, string uid)
    {
        var storedLines = CalDavIcsLines.Parse(storedIcs);
        var storedBlocks = CalDavIcsLines.TopLevelComponents(storedLines);
        var stored = KeyedEvents(storedIcs, storedLines, storedBlocks, uid);
        var copyLines = CalDavIcsLines.Parse(copyIcs);
        var copyBlocks = CalDavIcsLines.TopLevelComponents(copyLines);
        var incoming = KeyedEvents(copyIcs, copyLines, copyBlocks, uid);

        var storedSeries = stored.FirstOrDefault(value => value.Key.Length == 0);
        var replacements = new Dictionary<int, List<string>>();
        var additions = new List<string>();

        foreach (var component in incoming)
        {
            var current = stored.FirstOrDefault(value => string.Equals(value.Key, component.Key, StringComparison.Ordinal));
            if (current == null)
                additions.AddRange(ComposeEvent(copyLines, component.Block, storedLines, storedSeries?.Block));
            else if (component.Sequence > current.Sequence)
                replacements[current.Block.BeginIndex] = ComposeEvent(copyLines, component.Block, storedLines, current.Block);
        }

        if (replacements.Count == 0 && additions.Count == 0)
            return storedIcs;

        var storedZones = storedBlocks
            .Where(block => block.Name == "VTIMEZONE")
            .Select(block => TimeZoneId(storedLines, block))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newZones = copyBlocks
            .Where(block => block.Name == "VTIMEZONE" && TimeZoneId(copyLines, block) is { } tzId && !storedZones.Contains(tzId))
            .SelectMany(block => RawLines(copyLines, block))
            .ToList();

        var byBegin = storedBlocks.ToDictionary(block => block.BeginIndex);
        var calendarEnd = CalDavIcsLines.CalendarEndIndex(storedLines);
        var output = new List<string>(storedLines.Count + additions.Count + newZones.Count);

        for (var index = 0; index < storedLines.Count; index++)
        {
            if (index == calendarEnd)
            {
                output.AddRange(newZones);
                output.AddRange(additions);
            }

            if (byBegin.TryGetValue(index, out var block))
            {
                output.AddRange(replacements.TryGetValue(index, out var replacement) ? replacement : RawLines(storedLines, block));
                index = block.EndIndex;
                continue;
            }

            if (storedLines[index].Name == "METHOD")
                continue;

            output.Add(storedLines[index].Raw);
        }

        return CalDavIcsLines.Join(output);
    }

    /// <summary>
    /// An incoming component with the user's own data of <paramref name="carryFrom"/>: its alarms and
    /// other subcomponents replace the incoming ones, its user-owned properties replace the incoming
    /// ones, and its X- properties the incoming component lacks are added.
    /// </summary>
    private static List<string> ComposeEvent(IReadOnlyList<IcsLine> lines, IcsBlock block, IReadOnlyList<IcsLine> carryLines, IcsBlock? carryFrom)
    {
        var output = new List<string> { lines[block.BeginIndex].Raw };
        var properties = block.Properties(lines).ToList();

        if (carryFrom == null)
        {
            output.AddRange(properties.Select(line => line.Raw));
            output.AddRange(SubcomponentLines(lines, block));
            output.Add(lines[block.EndIndex].Raw);
            return output;
        }

        var carried = carryFrom.Properties(carryLines).ToList();
        var ownNames = carried.Select(line => line.Name).Where(UserOwnedProperties.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomingNames = properties.Select(line => line.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        output.AddRange(properties.Where(line => !ownNames.Contains(line.Name)).Select(line => line.Raw));
        output.AddRange(carried
            .Where(line => ownNames.Contains(line.Name) ||
                           (line.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) && !incomingNames.Contains(line.Name)))
            .Select(line => line.Raw));
        output.AddRange(SubcomponentLines(carryLines, carryFrom));
        output.Add(lines[block.EndIndex].Raw);
        return output;
    }

    /// <summary>The lines of a component's subcomponents (VALARM), without its own properties.</summary>
    private static IEnumerable<string> SubcomponentLines(IReadOnlyList<IcsLine> lines, IcsBlock block)
    {
        if (block.FirstChildIndex < 0)
            yield break;

        var properties = block.PropertyIndexes.ToHashSet();
        for (var index = block.FirstChildIndex; index < block.EndIndex; index++)
        {
            if (!properties.Contains(index))
                yield return lines[index].Raw;
        }
    }

    /// <summary>The VEVENT blocks of <paramref name="uid"/> with their RECURRENCE-ID key (empty for the series) and SEQUENCE.</summary>
    private static List<KeyedEvent> KeyedEvents(string icsContent, IReadOnlyList<IcsLine> lines, IReadOnlyList<IcsBlock> blocks, string uid)
    {
        var events = IcalCalendar.Load(icsContent)?.Events.ToList()
                     ?? throw new InvalidOperationException("The calendar object is not valid iCalendar.");
        var eventBlocks = blocks.Where(block => block.Name == "VEVENT").ToList();
        if (eventBlocks.Count != events.Count)
            throw new InvalidOperationException("The calendar object could not be matched to its events.");

        var result = new List<KeyedEvent>();
        for (var index = 0; index < events.Count; index++)
        {
            var calendarEvent = events[index];
            if (calendarEvent == null || !string.Equals(calendarEvent.Uid, uid, StringComparison.Ordinal))
                continue;

            var key = calendarEvent.RecurrenceIdentifier?.StartTime is { } recurrenceId ? CalDavOccurrenceKey.Create(recurrenceId) : string.Empty;
            result.Add(new KeyedEvent(key, calendarEvent.Sequence, eventBlocks[index]));
        }

        return result;
    }

    private static string? TimeZoneId(IReadOnlyList<IcsLine> lines, IcsBlock block)
        => block.Properties(lines).FirstOrDefault(line => line.Name == "TZID")?.Value.Trim();

    private sealed record KeyedEvent(string Key, int Sequence, IcsBlock Block);

    private static Dictionary<string, List<IcsOutputLine>?> OrganizerChanges(IReadOnlyList<IcsLine> lines, IcsBlock block)
    {
        var changes = new Dictionary<string, List<IcsOutputLine>?>(StringComparer.OrdinalIgnoreCase);
        var organizer = block.Properties(lines).FirstOrDefault(line => line.Name == "ORGANIZER");

        if (organizer != null)
            changes["ORGANIZER"] = [IcsOutputLine.New(organizer.WithParameters(Parameters(("SCHEDULE-AGENT", "CLIENT"), ("SCHEDULE-STATUS", null))))];

        return changes;
    }

    private static string CreateAttendeeLine(string address, string? name, string partStat)
    {
        var line = "ATTENDEE";
        if (!string.IsNullOrWhiteSpace(name))
            line += $";CN={CalDavIcsLines.ParameterValue(name.Trim())}";

        return $"{line};ROLE=REQ-PARTICIPANT;PARTSTAT={partStat}:mailto:{address}";
    }

    private static bool IsEventOf(IcsBlock block, IReadOnlyList<IcsLine> lines, string uid)
        => block.Name == "VEVENT" &&
           string.Equals(block.Properties(lines).FirstOrDefault(line => line.Name == "UID")?.Value.Trim(), uid, StringComparison.Ordinal);

    private static IEnumerable<string> RawLines(IReadOnlyList<IcsLine> lines, IcsBlock block)
    {
        for (var index = block.BeginIndex; index <= block.EndIndex; index++)
            yield return lines[index].Raw;
    }

    private static Dictionary<string, string?> Parameters(params (string Name, string? Value)[] values)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
            result[name] = value;

        return result;
    }
}
