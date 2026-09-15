using System.Text;

namespace Wino.Mapi.Calendar;

/// <summary>
/// The time-zone blobs an appointment carries (MS-OXOCAL 2.2.1.39 TZREG and 2.2.1.41 TZDEFINITION),
/// built from a Windows <see cref="TimeZoneInfo"/>. The definition names the zone by its Windows key
/// and carries the rule in force at the appointment's date; the struct is the same rule without a
/// name. Both are what Outlook writes and what this client reads back through TimeZoneKeyName.
/// </summary>
public static class TimeZoneDefinition
{
    private const byte MajorVersion = 0x02;
    private const byte MinorVersion = 0x01;
    private const ushort RuleSize = 0x003E;

    /// <summary>TZRULE flags: the rule that applies to the appointment, and the one the client's registry holds now.</summary>
    private const ushort RuleFlagRecurCurrent = 0x0001;
    private const ushort RuleFlagEffective = 0x0002;

    /// <summary>TZDEFINITION for <paramref name="zone"/>, with one rule: the one in force at <paramref name="atUtc"/>.</summary>
    public static byte[] Encode(TimeZoneInfo zone, DateTime atUtc)
    {
        var keyName = Encoding.Unicode.GetBytes(zone.Id);
        var rule = EncodeRule(zone, atUtc);

        var header = 2 + 2 + keyName.Length + 2;       // Reserved, cchKeyName, KeyName, cRules
        var blob = new byte[1 + 1 + 2 + header + rule.Length];
        var o = 0;
        blob[o++] = MajorVersion;
        blob[o++] = MinorVersion;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)header); o += 2;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)0x0002); o += 2;      // Reserved
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)zone.Id.Length); o += 2;
        keyName.CopyTo(blob, o); o += keyName.Length;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)1); o += 2;           // cRules
        rule.CopyTo(blob, o);
        return blob;
    }

    /// <summary>TZREG (PidLidTimeZoneStruct) for the rule in force at <paramref name="atUtc"/>: 48 bytes.</summary>
    public static byte[] EncodeStruct(TimeZoneInfo zone, DateTime atUtc)
    {
        var (bias, standardBias, daylightBias, standard, daylight) = Resolve(zone, atUtc);
        var blob = new byte[48];
        BitConverter.TryWriteBytes(blob.AsSpan(0, 4), bias);
        BitConverter.TryWriteBytes(blob.AsSpan(4, 4), standardBias);
        BitConverter.TryWriteBytes(blob.AsSpan(8, 4), daylightBias);
        // wStandardYear (0), stStandardDate, wDaylightYear (0), stDaylightDate
        standard.CopyTo(blob, 14);
        daylight.CopyTo(blob, 32);
        return blob;
    }

    /// <summary>One TZRULE: versions, size, flags, year, 14 reserved bytes, the three biases, the two transition dates.</summary>
    private static byte[] EncodeRule(TimeZoneInfo zone, DateTime atUtc)
    {
        var (bias, standardBias, daylightBias, standard, daylight) = Resolve(zone, atUtc);
        var year = RuleYear(zone, atUtc);

        var blob = new byte[4 + RuleSize];
        var o = 0;
        blob[o++] = MajorVersion;
        blob[o++] = MinorVersion;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), RuleSize); o += 2;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)(RuleFlagRecurCurrent | RuleFlagEffective)); o += 2;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 2), (ushort)year); o += 2;
        o += 14;                                                                     // X: reserved
        BitConverter.TryWriteBytes(blob.AsSpan(o, 4), bias); o += 4;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 4), standardBias); o += 4;
        BitConverter.TryWriteBytes(blob.AsSpan(o, 4), daylightBias); o += 4;
        standard.CopyTo(blob, o); o += 16;
        daylight.CopyTo(blob, o);
        return blob;
    }

    /// <summary>
    /// Biases in minutes the MAPI way (UTC = local + bias, so Eastern is +300) and the two transition
    /// SYSTEMTIMEs in day-in-month form; a zone without daylight saving gets zero dates.
    /// </summary>
    private static (int Bias, int StandardBias, int DaylightBias, byte[] Standard, byte[] Daylight) Resolve(TimeZoneInfo zone, DateTime atUtc)
    {
        var rule = RuleAt(zone, atUtc);
        var baseOffset = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        var bias = -(int)baseOffset.TotalMinutes;

        if (rule is null || rule.DaylightDelta == TimeSpan.Zero)
            return (bias, 0, 0, new byte[16], new byte[16]);

        return (bias, 0, -(int)rule.DaylightDelta.TotalMinutes, SystemTime(rule.DaylightTransitionEnd), SystemTime(rule.DaylightTransitionStart));
    }

    /// <summary>The adjustment rule in force at <paramref name="atUtc"/>, or null when the zone has none.</summary>
    private static TimeZoneInfo.AdjustmentRule? RuleAt(TimeZoneInfo zone, DateTime atUtc)
        => zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= atUtc.Date && atUtc.Date <= r.DateEnd);

    /// <summary>The year a rule is tagged with: its start year, or 1601 for the zone's timeless rule.</summary>
    private static int RuleYear(TimeZoneInfo zone, DateTime atUtc)
    {
        var year = RuleAt(zone, atUtc)?.DateStart.Year ?? 1601;
        return year <= 1 ? 1601 : year;
    }

    /// <summary>SYSTEMTIME in the TZI day-in-month convention: wYear 0, wDay = week of month (5 = last).</summary>
    private static byte[] SystemTime(TimeZoneInfo.TransitionTime transition)
    {
        var blob = new byte[16];
        BitConverter.TryWriteBytes(blob.AsSpan(2, 2), (ushort)transition.Month);
        if (transition.IsFixedDateRule)
        {
            // Rare on Windows; approximated as the week that holds the day.
            BitConverter.TryWriteBytes(blob.AsSpan(4, 2), (ushort)0);
            BitConverter.TryWriteBytes(blob.AsSpan(6, 2), (ushort)Math.Clamp((transition.Day + 6) / 7, 1, 5));
        }
        else
        {
            BitConverter.TryWriteBytes(blob.AsSpan(4, 2), (ushort)transition.DayOfWeek);
            BitConverter.TryWriteBytes(blob.AsSpan(6, 2), (ushort)transition.Week);
        }
        BitConverter.TryWriteBytes(blob.AsSpan(8, 2), (ushort)transition.TimeOfDay.Hour);
        BitConverter.TryWriteBytes(blob.AsSpan(10, 2), (ushort)transition.TimeOfDay.Minute);
        BitConverter.TryWriteBytes(blob.AsSpan(12, 2), (ushort)transition.TimeOfDay.Second);
        return blob;
    }

    /// <summary>The Windows zone for an IANA or Windows id, or the machine's zone when unknown or missing.</summary>
    public static TimeZoneInfo Resolve(string? ianaOrWindowsId)
    {
        if (string.IsNullOrWhiteSpace(ianaOrWindowsId))
            return TimeZoneInfo.Local;
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaOrWindowsId, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            return TimeZoneInfo.FindSystemTimeZoneById(ianaOrWindowsId);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}
