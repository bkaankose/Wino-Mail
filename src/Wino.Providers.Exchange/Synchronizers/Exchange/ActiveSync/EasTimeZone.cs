using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// The 172-byte TIME_ZONE_INFORMATION blob of MS-ASDTYPE. Reading maps the blob to a system zone with the same
/// offsets over the year; writing derives the offsets and transitions by probing the zone, so neither depends on
/// Windows adjustment rules and both work on every platform.
/// </summary>
internal static class EasTimeZone
{
    private const int BlobLength = 172;
    private static readonly ConcurrentDictionary<(string Blob, int Year), string?> ReadCache = new();

    /// <summary>The zone the event's times are in: UTC when the event names none or one no system zone matches.</summary>
    internal static TimeZoneInfo Read(XElement data, int year)
    {
        var text = EasDataMapper.Value(data, EasDataMapper.Calendar, "TimeZone");
        if (string.IsNullOrEmpty(text))
            return TimeZoneInfo.Utc;

        var id = ReadCache.GetOrAdd((text, year), key => Match(key.Blob, key.Year));
        return id != null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>The IANA id for a zone, which calendar rows store; null for UTC.</summary>
    internal static string? IanaId(TimeZoneInfo zone)
    {
        if (zone == TimeZoneInfo.Utc || zone.Id is "UTC" or "Etc/UTC")
            return null;
        if (zone.HasIanaId)
            return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    /// <summary>The zone a stored row names, IANA or Windows id; UTC when it names none or one this platform lacks.</summary>
    internal static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return TimeZoneInfo.Utc;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
            return zone;
        return TimeZoneInfo.Utc;
    }

    private static string? Match(string text, int year)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
        if (bytes.Length != BlobLength)
            return null;

        var bias = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var standardBias = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(84));
        var daylightBias = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(168));
        var standardOffset = TimeSpan.FromMinutes(-(long)bias - standardBias);
        if (standardOffset < TimeSpan.FromHours(-14) || standardOffset > TimeSpan.FromHours(14))
            return null;

        var daylight = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(154)) == 0 ? null
            : ReadTransition(bytes.AsSpan(152, 16), year);
        var standard = daylight == null ? null : ReadTransition(bytes.AsSpan(68, 16), year);
        var daylightOffset = TimeSpan.FromMinutes(-(long)bias - daylightBias);

        TimeSpan Expected(DateTime utc)
        {
            if (daylight is not { } start || standard is not { } end)
                return standardOffset;
            // Transition wall times are in the offset in force before them.
            var daylightStartUtc = start - standardOffset;
            var daylightEndUtc = end - daylightOffset;
            var inDaylight = daylightStartUtc < daylightEndUtc
                ? utc >= daylightStartUtc && utc < daylightEndUtc
                : utc >= daylightStartUtc || utc < daylightEndUtc;
            return inDaylight ? daylightOffset : standardOffset;
        }

        var probes = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365)
            .Select(day => new DateTime(year, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day)).ToArray();
        var expected = probes.Select(Expected).ToArray();
        bool Matches(TimeZoneInfo zone) => probes.Select((date, index) => zone.GetUtcOffset(date) == expected[index]).All(match => match);

        var name = ReadName(bytes.AsSpan(4, 64));
        var candidates = new List<TimeZoneInfo> { TimeZoneInfo.Local };
        candidates.AddRange(TimeZoneInfo.GetSystemTimeZones().Where(zone => zone.Id == name || zone.StandardName == name));
        candidates.AddRange(TimeZoneInfo.GetSystemTimeZones());
        return candidates.FirstOrDefault(Matches)?.Id;
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>The wall-clock moment of a transition in <paramref name="year"/>, or null when the blob's rule is invalid.</summary>
    private static DateTime? ReadTransition(ReadOnlySpan<byte> bytes, int year)
    {
        var absoluteYear = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var month = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var weekday = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var day = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var hour = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        var minute = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (month is < 1 or > 12 || weekday > 6 || hour > 23 || minute > 59)
            return null;

        var time = new TimeSpan(hour, minute, 0);
        if (absoluteYear != 0)
            return day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day) + time : null;
        if (day is < 1 or > 5)
            return null;

        var first = new DateTime(year, month, 1);
        var date = first.AddDays(((weekday - (int)first.DayOfWeek) + 7) % 7 + (day - 1) * 7);
        while (date.Month != month)
            date = date.AddDays(-7);
        return date + time;
    }

    /// <summary>The blob for <paramref name="zone"/> in the year of <paramref name="referenceUtc"/>, from the offsets the zone reports.</summary>
    internal static string Write(TimeZoneInfo zone, DateTime referenceUtc)
    {
        var bytes = new byte[BlobLength];
        var start = new DateTime(referenceUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddYears(1);
        var transitions = new List<(DateTime Utc, TimeSpan Before, TimeSpan After)>();
        var previous = zone.GetUtcOffset(start);
        for (var probe = start.AddHours(1); probe <= end; probe = probe.AddHours(1))
        {
            var offset = zone.GetUtcOffset(probe);
            if (offset == previous)
                continue;

            // Narrow the hour down to the minute the offset changes.
            var low = probe.AddHours(-1);
            while (probe - low > TimeSpan.FromMinutes(1))
            {
                var middle = low + TimeSpan.FromTicks((probe - low).Ticks / 2);
                if (zone.GetUtcOffset(middle) == previous) low = middle; else probe = middle;
            }
            transitions.Add((probe, previous, offset));
            previous = offset;
        }

        // A year without exactly one change each way has no daylight rule; the offset in force at the event is used.
        var hasDaylight = transitions.Count == 2 && transitions[0].After - transitions[0].Before == -(transitions[1].After - transitions[1].Before);
        var standardOffset = hasDaylight
            ? (transitions[0].Before < transitions[0].After ? transitions[0].Before : transitions[0].After)
            : zone.GetUtcOffset(DateTime.SpecifyKind(referenceUtc, DateTimeKind.Utc));

        BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)-standardOffset.TotalMinutes));
        WriteName(bytes.AsSpan(4, 64), zone.StandardName);
        WriteName(bytes.AsSpan(88, 64), zone.DaylightName);
        if (hasDaylight)
        {
            var toDaylight = transitions.First(transition => transition.After > transition.Before);
            var toStandard = transitions.First(transition => transition.After < transition.Before);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(168), checked((int)-(toDaylight.After - toDaylight.Before).TotalMinutes));
            WriteTransition(bytes.AsSpan(68, 16), toStandard.Utc + toStandard.Before);
            WriteTransition(bytes.AsSpan(152, 16), toDaylight.Utc + toDaylight.Before);
        }
        return Convert.ToBase64String(bytes);
    }

    private static void WriteName(Span<byte> target, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        var text = name.Length > 31 ? name[..31] : name;
        Encoding.Unicode.GetBytes(text, target);
    }

    /// <summary>A recurring rule (year 0): month, weekday, its week in the month (5 = last) and the wall-clock time.</summary>
    private static void WriteTransition(Span<byte> bytes, DateTime wallClock)
    {
        var week = (wallClock.Day - 1) / 7 + 1;
        if (wallClock.Day + 7 > DateTime.DaysInMonth(wallClock.Year, wallClock.Month))
            week = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], checked((ushort)wallClock.Month));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], checked((ushort)wallClock.DayOfWeek));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], checked((ushort)week));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], checked((ushort)wallClock.Hour));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], checked((ushort)wallClock.Minute));
    }
}
