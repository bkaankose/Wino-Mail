using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Wino.Core.Synchronizers.Exchange.ActiveSync;

internal static class EasTimeZone
{
    internal static TimeZoneInfo Read(XElement data, int year)
    {
        var text = EasDataMapper.Value(data, EasDataMapper.Calendar, "TimeZone");
        if (string.IsNullOrEmpty(text))
            return TimeZoneInfo.Utc;
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length != 172)
            throw new InvalidDataException("The EAS event timezone is invalid.");
        var bias = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var standardBias = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(84));
        var daylightBias = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(168));
        var offset = TimeSpan.FromMinutes(-(long)bias - standardBias);
        TimeZoneInfo custom;
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(154)) == 0)
            custom = TimeZoneInfo.CreateCustomTimeZone("EAS", offset, "EAS", "EAS");
        else
        {
            var daylight = ReadTransition(bytes.AsSpan(152, 16), year);
            var standard = ReadTransition(bytes.AsSpan(68, 16), year);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(year, 1, 1), new DateTime(year, 12, 31),
                TimeSpan.FromMinutes((long)standardBias - daylightBias), daylight, standard);
            custom = TimeZoneInfo.CreateCustomTimeZone("EAS", offset, "EAS", "EAS", "EAS daylight", new[] { rule });
        }

        var probes = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365).Select(day => new DateTime(year, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day)).ToArray();
        var match = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(zone => probes.All(date => zone.GetUtcOffset(date) == custom.GetUtcOffset(date)));
        return match ?? throw new NotSupportedException("The EAS event timezone cannot be mapped to a system timezone.");
    }

    private static TimeZoneInfo.TransitionTime ReadTransition(ReadOnlySpan<byte> bytes, int eventYear)
    {
        var year = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var month = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var weekday = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var day = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var time = new DateTime(1, 1, 1, BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]));
        if (year != 0)
        {
            if (year != eventYear)
                throw new NotSupportedException("An absolute EAS timezone transition belongs to a different year.");
            return TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, month, day);
        }
        return TimeZoneInfo.TransitionTime.CreateFloatingDateRule(time, month, day, (DayOfWeek)weekday);
    }

    internal static string Write(TimeZoneInfo zone, int year)
    {
        var bytes = new byte[172];
        var rule = zone.GetAdjustmentRules().FirstOrDefault(rule => rule.DateStart.Year <= year && rule.DateEnd.Year >= year);
        var offset = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)-offset.TotalMinutes));
        if (rule != null && rule.DaylightDelta != TimeSpan.Zero)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(168), checked((int)-rule.DaylightDelta.TotalMinutes));
            WriteTransition(bytes.AsSpan(68, 16), rule.DaylightTransitionEnd, year);
            WriteTransition(bytes.AsSpan(152, 16), rule.DaylightTransitionStart, year);
        }
        return Convert.ToBase64String(bytes);
    }

    private static void WriteTransition(Span<byte> bytes, TimeZoneInfo.TransitionTime transition, int year)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, transition.IsFixedDateRule ? checked((ushort)year) : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], checked((ushort)transition.Month));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], checked((ushort)transition.DayOfWeek));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], checked((ushort)(transition.IsFixedDateRule ? transition.Day : transition.Week)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], checked((ushort)transition.TimeOfDay.Hour));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], checked((ushort)transition.TimeOfDay.Minute));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], checked((ushort)transition.TimeOfDay.Second));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[14..], checked((ushort)transition.TimeOfDay.Millisecond));
    }
}
