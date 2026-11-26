using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// The 172-byte TimeZone structure of MS-ASDTYPE. Reading builds the zone the structure describes (bias, standard and
/// daylight biases and transition rules); a system zone takes its place only where it is exactly equivalent, because
/// calendar rows name system zones. Writing derives the offsets and transitions by probing the zone, so neither
/// depends on Windows adjustment rules and both work on every platform.
/// </summary>
internal static class EasTimeZone
{
    private const int BlobLength = 172;
    private static readonly ConcurrentDictionary<(string Blob, int Year), EasEventZone> ReadCache = new();

    /// <summary>
    /// The zone of an event's times in <paramref name="year"/>. An event without a TimeZone element is in UTC, and so
    /// is one whose element is not a valid structure (logged). A valid structure no system zone matches keeps its own
    /// zone for the event's rule and writes; its rows store the times in UTC.
    /// </summary>
    internal static EasEventZone Read(XElement data, int year)
    {
        var text = EasDataMapper.Value(data, EasDataMapper.Calendar, "TimeZone");
        if (string.IsNullOrEmpty(text))
            return new EasEventZone(TimeZoneInfo.Utc, TimeZoneInfo.Utc);
        return ReadCache.GetOrAdd((text, year), key => ReadCore(key.Blob, key.Year));
    }

    private static EasEventZone ReadCore(string text, int year)
    {
        var zone = FromBlob(text);
        if (zone == null)
        {
            Serilog.Log.Warning("An EAS event time zone is not a valid TimeZone structure; its times are read as UTC.");
            return new EasEventZone(TimeZoneInfo.Utc, TimeZoneInfo.Utc);
        }
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime)
            return new EasEventZone(TimeZoneInfo.Utc, TimeZoneInfo.Utc);

        var system = Equivalent(zone, year);
        if (system == null)
            Serilog.Log.Information("No system time zone is equivalent to the EAS event zone {Zone}; the event keeps its own zone and its rows store UTC times.", zone.DisplayName);
        return new EasEventZone(system ?? zone, system ?? TimeZoneInfo.Utc);
    }

    /// <summary>
    /// The zone a TimeZone structure describes: the base offset is -(Bias + StandardBias), daylight time is
    /// -(Bias + DaylightBias), and the transitions are recurring (year 0: the wDay-th wDayOfWeek of the month, 5 the
    /// last) or absolute dates of one year. Null when the structure is not valid.
    /// </summary>
    internal static TimeZoneInfo? FromBlob(string text)
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
        var daylightOffset = TimeSpan.FromMinutes(-(long)bias - daylightBias);
        if (standardOffset < TimeSpan.FromHours(-14) || standardOffset > TimeSpan.FromHours(14) ||
            daylightOffset < TimeSpan.FromHours(-14) || daylightOffset > TimeSpan.FromHours(14))
            return null;

        var standardName = ReadName(bytes.AsSpan(4, 64));
        var daylightName = ReadName(bytes.AsSpan(88, 64));
        var label = (standardOffset < TimeSpan.Zero ? "-" : "+") + standardOffset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        var id = "EAS " + Convert.ToHexString(SHA256.HashData(bytes), 0, 8);
        var display = string.IsNullOrWhiteSpace(standardName) ? "(UTC" + label + ")" : "(UTC" + label + ") " + standardName;
        try
        {
            var standardMonth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(70));
            var daylightMonth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(154));
            if (standardMonth == 0 || daylightMonth == 0 || daylightOffset == standardOffset)
                return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, display, standardName);

            var toDaylight = ReadRule(bytes.AsSpan(152, 16), out var daylightYear);
            var toStandard = ReadRule(bytes.AsSpan(68, 16), out var standardYear);
            if (toDaylight == null || toStandard == null || daylightYear != standardYear)
                return null;
            var from = daylightYear == 0 ? DateTime.MinValue.Date : new DateTime(daylightYear, 1, 1);
            var to = daylightYear == 0 ? DateTime.MaxValue.Date : new DateTime(daylightYear, 12, 31);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(from, to, daylightOffset - standardOffset, toDaylight.Value, toStandard.Value);
            return TimeZoneInfo.CreateCustomTimeZone(id, standardOffset, display, standardName, daylightName, [rule]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>A system zone with the same offset as <paramref name="zone"/> twice a day through the year and on both sides of each of its transitions.</summary>
    private static TimeZoneInfo? Equivalent(TimeZoneInfo zone, int year)
    {
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var probes = new List<DateTime>();
        for (var moment = start; moment < start.AddYears(1); moment = moment.AddHours(12))
            probes.Add(moment);
        var previous = zone.GetUtcOffset(start);
        for (var hour = start.AddHours(1); hour <= start.AddYears(1); hour = hour.AddHours(1))
        {
            var offset = zone.GetUtcOffset(hour);
            if (offset == previous)
                continue;
            var low = hour.AddHours(-1);
            var high = hour;
            while (high - low > TimeSpan.FromMinutes(1))
            {
                var middle = low + TimeSpan.FromTicks((high - low).Ticks / 2);
                if (zone.GetUtcOffset(middle) == previous) low = middle; else high = middle;
            }
            probes.Add(low);
            probes.Add(high);
            previous = offset;
        }
        var expected = probes.Select(zone.GetUtcOffset).ToArray();
        bool Matches(TimeZoneInfo candidate) => probes.Select((date, index) => candidate.GetUtcOffset(date) == expected[index]).All(match => match);

        var candidates = new List<TimeZoneInfo> { TimeZoneInfo.Local };
        candidates.AddRange(TimeZoneInfo.GetSystemTimeZones().Where(candidate => candidate.StandardName == zone.StandardName));
        candidates.AddRange(TimeZoneInfo.GetSystemTimeZones());
        return candidates.FirstOrDefault(Matches);
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

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>
    /// A transition of the structure: wYear 0 is the wDay-th wDayOfWeek of wMonth every year (5 the last), any other
    /// year the date wDay of wMonth in that year only. Null when the fields are out of range.
    /// </summary>
    private static TimeZoneInfo.TransitionTime? ReadRule(ReadOnlySpan<byte> bytes, out int year)
    {
        year = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var month = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var weekday = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var day = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var hour = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        var minute = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        var second = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var milliseconds = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        if (month is < 1 or > 12 || weekday > 6 || hour > 23 || minute > 59 || second > 59 || milliseconds > 999 || year > 9999)
            return null;

        var time = new DateTime(1, 1, 1, hour, minute, second, milliseconds);
        if (year != 0)
            return day >= 1 && day <= DateTime.DaysInMonth(year, month) ? TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, month, day) : null;
        return day is >= 1 and <= 5 ? TimeZoneInfo.TransitionTime.CreateFloatingDateRule(time, month, day, (DayOfWeek)weekday) : null;
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

/// <summary>
/// An event's zone: Zone evaluates its times and rule, Stored is the system zone its rows name (UTC when no system
/// zone is equivalent to the event's).
/// </summary>
internal sealed record EasEventZone(TimeZoneInfo Zone, TimeZoneInfo Stored);
