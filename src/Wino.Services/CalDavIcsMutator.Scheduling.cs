#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Ical.Net.CalendarComponents;
using Wino.Core.Domain.Enums;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Services;

/// <summary>
/// The other writes Wino makes to a stored event, line by line like <see cref="CalDavIcsMutator.EditEvent"/>:
/// the user's own answer to an invitation, and removing one occurrence of a series.
/// </summary>
public static partial class CalDavIcsMutator
{
    /// <summary>
    /// Writes the user's answer the way RFC 6638 section 3.2 expects from an attendee: only the
    /// PARTSTAT of the user's own ATTENDEE lines changes and DTSTAMP is refreshed. Answering the
    /// series also answers every exception that lists the user; answering one occurrence adds an
    /// exception for it when there is none yet. Every other line stays as stored.
    /// </summary>
    private static string SetOwnParticipationLines(string existingIcs, string remoteEventId, CalDavUserIdentity identity, AttendeeStatus status, string addedAddress)
    {
        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var originalEvents = calendar.Events.ToList();
        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
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

        var lines = CalDavIcsLines.Parse(existingIcs);
        var eventBlocks = RequireEventBlocks(lines, originalEvents);
        var partStat = MapParticipationStatus(status);
        var stamp = CalDavIcsLines.FormatUtc(DateTime.UtcNow);
        var patches = new Dictionary<IcsBlock, Dictionary<string, List<IcsOutputLine>?>>();

        Dictionary<string, List<IcsOutputLine>?>? ParticipationChanges(IcsBlock block, bool addWhenMissing)
        {
            var attendees = block.Properties(lines).Where(line => line.Name == "ATTENDEE").ToList();
            var result = new List<IcsOutputLine>();
            var own = 0;
            foreach (var line in attendees)
            {
                if (!identity.IsUser(line.Value))
                {
                    result.Add(IcsOutputLine.Keep(line));
                    continue;
                }

                own++;
                result.Add(IcsOutputLine.New(line.WithParameters(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PARTSTAT"] = partStat })));
            }

            if (own == 0)
            {
                if (!addWhenMissing || string.IsNullOrWhiteSpace(addedAddress))
                    return null;

                result.Add(IcsOutputLine.New($"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT={partStat}:mailto:{addedAddress}"));
            }

            return new Dictionary<string, List<IcsOutputLine>?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ATTENDEE"] = result,
                ["DTSTAMP"] = [IcsOutputLine.New($"DTSTAMP:{stamp}")]
            };
        }

        List<string>? newBlock = null;
        if (master != null)
        {
            var masterBlock = eventBlocks[IndexOfReference(originalEvents, master)];
            var changes = ParticipationChanges(masterBlock, addWhenMissing: true)
                          ?? throw new InvalidOperationException("The user is not an attendee of the CalDAV event.");
            var organizer = masterBlock.Properties(lines).FirstOrDefault(line => line.Name == "ORGANIZER");
            changes["ORGANIZER"] = organizer == null ? null : [IcsOutputLine.Keep(organizer)];
            newBlock = ApplySerializedProperties(calendar, target, changes, recurrenceChanged: false, isNewException: true);
        }
        else
        {
            var targetBlock = eventBlocks[IndexOfReference(originalEvents, target)];
            if (ParticipationChanges(targetBlock, addWhenMissing: true) is { } changes)
                patches[targetBlock] = changes;

            if (target.RecurrenceIdentifier == null)
            {
                // An exception without the user means the organizer removed them from that instance.
                for (var index = 0; index < originalEvents.Count; index++)
                {
                    var calendarEvent = originalEvents[index];
                    if (ReferenceEquals(calendarEvent, target) || calendarEvent.RecurrenceIdentifier == null ||
                        !string.Equals(calendarEvent.Uid, uid, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ParticipationChanges(eventBlocks[index], addWhenMissing: false) is { } exceptionChanges)
                        patches[eventBlocks[index]] = exceptionChanges;
                }
            }
        }

        return Assemble(lines, patches, removed: null, newBlock);
    }

    /// <summary>
    /// Removes one occurrence: the master gets an EXDATE in the value type of its DTSTART (sabre/dav
    /// and iCloud do not match a UTC EXDATE against a zoned or all-day series) and an exception stored
    /// for that occurrence is dropped. SEQUENCE belongs to the organizer (RFC 5545 section 3.8.7.4):
    /// it goes up only when the user organizes the event or it has no ORGANIZER, so an attendee's copy
    /// keeps the organizer's value and later iMIP updates still compare against it.
    /// </summary>
    private static string RemoveOccurrenceLines(string existingIcs, string remoteEventId, CalDavUserIdentity? identity)
    {
        var (uid, occurrenceKey) = SplitRemoteEventId(remoteEventId);
        if (string.IsNullOrWhiteSpace(occurrenceKey))
            throw new ArgumentException("A recurring occurrence ID is required.", nameof(remoteEventId));

        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("The stored CalDAV resource is not a valid iCalendar object.");
        var originalEvents = calendar.Events.ToList();
        var master = FindMaster(calendar, uid)
            ?? throw new InvalidOperationException("The recurring CalDAV master event is missing from the stored resource.");
        var exception = FindEvent(calendar, uid, occurrenceKey);

        var lines = CalDavIcsLines.Parse(existingIcs);
        var eventBlocks = RequireEventBlocks(lines, originalEvents);
        var masterBlock = eventBlocks[IndexOfReference(originalEvents, master)];
        var masterStart = master.Start ?? throw new InvalidOperationException("The recurring CalDAV master event has no DTSTART.");
        var exceptionDate = ToSeriesValueType(ParseOccurrenceKey(occurrenceKey, masterStart), masterStart);
        var stamp = CalDavIcsLines.FormatUtc(DateTime.UtcNow);

        var exceptionDates = masterBlock.Properties(lines).Where(line => line.Name == "EXDATE").Select(IcsOutputLine.Keep).ToList();
        exceptionDates.Add(IcsOutputLine.New(FormatDateTimeProperty("EXDATE", exceptionDate)));

        var masterChanges = new Dictionary<string, List<IcsOutputLine>?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXDATE"] = exceptionDates,
            ["DTSTAMP"] = [IcsOutputLine.New($"DTSTAMP:{stamp}")],
            ["LAST-MODIFIED"] = [IcsOutputLine.New($"LAST-MODIFIED:{stamp}")]
        };

        var organizer = master.Organizer?.Value?.OriginalString;
        if (identity == null || string.IsNullOrWhiteSpace(organizer) || identity.IsUser(organizer))
            masterChanges["SEQUENCE"] = [IcsOutputLine.New($"SEQUENCE:{master.Sequence + 1}")];

        var patches = new Dictionary<IcsBlock, Dictionary<string, List<IcsOutputLine>?>>
        {
            [masterBlock] = masterChanges
        };

        var removed = exception == null || exception.RecurrenceIdentifier == null
            ? null
            : eventBlocks[IndexOfReference(originalEvents, exception)];

        return Assemble(lines, patches, removed, newBlock: null);
    }

    private static List<IcsBlock> RequireEventBlocks(IReadOnlyList<IcsLine> lines, IReadOnlyList<CalendarEvent> events)
    {
        var blocks = CalDavIcsLines.TopLevelComponents(lines).Where(block => block.Name == "VEVENT").ToList();
        if (blocks.Count != events.Count)
            throw new InvalidOperationException("The stored CalDAV resource could not be matched to its events.");

        return blocks;
    }

    /// <summary>The document with patched blocks, one block left out, and a new component before END:VCALENDAR.</summary>
    private static string Assemble(
        IReadOnlyList<IcsLine> lines,
        IReadOnlyDictionary<IcsBlock, Dictionary<string, List<IcsOutputLine>?>> patches,
        IcsBlock? removed,
        List<string>? newBlock)
    {
        var byBegin = patches.ToDictionary(pair => pair.Key.BeginIndex);
        var calendarEnd = CalDavIcsLines.CalendarEndIndex(lines);
        var output = new List<string>(lines.Count + 8);

        for (var index = 0; index < lines.Count; index++)
        {
            if (index == calendarEnd && newBlock != null)
                output.AddRange(newBlock);

            if (removed != null && index == removed.BeginIndex)
            {
                index = removed.EndIndex;
                continue;
            }

            if (byBegin.TryGetValue(index, out var patch))
            {
                output.AddRange(CalDavIcsLines.PatchBlock(lines, patch.Key, patch.Value));
                index = patch.Key.EndIndex;
                continue;
            }

            output.Add(lines[index].Raw);
        }

        return CalDavIcsLines.Join(output);
    }
}
