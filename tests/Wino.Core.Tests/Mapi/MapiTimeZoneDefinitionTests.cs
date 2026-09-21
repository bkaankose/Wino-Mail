using Wino.Mapi;
using Wino.Mapi.Calendar;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The zone blobs written on appointments (MS-OXOCAL 2.2.1.39 TZREG, 2.2.1.41 TZDEFINITION) and their read-back.</summary>
public class MapiTimeZoneDefinitionTests
{
    private static readonly DateTime Summer = new(2026, 9, 10, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Eastern_DefinitionCarriesKeyNameBiasesAndDstTransitions()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var blob = TimeZoneDefinition.Encode(zone, Summer);

        blob[0].Should().Be(0x02, "MajorVersion");
        blob[1].Should().Be(0x01, "MinorVersion");
        var keyBytes = zone.Id.Length * 2;
        BitConverter.ToUInt16(blob, 2).Should().Be((ushort)(2 + 2 + keyBytes + 2), "cbHeader");
        BitConverter.ToUInt16(blob, 4).Should().Be(0x0002, "Reserved");
        BitConverter.ToUInt16(blob, 6).Should().Be((ushort)zone.Id.Length, "cchKeyName");
        var afterKey = 8 + keyBytes;
        BitConverter.ToUInt16(blob, afterKey).Should().Be(1, "cRules");

        var rule = afterKey + 2;
        blob[rule].Should().Be(0x02);
        blob[rule + 1].Should().Be(0x01);
        BitConverter.ToUInt16(blob, rule + 2).Should().Be(0x003E, "cbRule");
        BitConverter.ToUInt16(blob, rule + 4).Should().Be(0x0003, "effective + current");
        BitConverter.ToUInt16(blob, rule + 6).Should().BeGreaterThan(1600, "wYear");
        var biases = rule + 8 + 14;
        BitConverter.ToInt32(blob, biases).Should().Be(300, "lBias: UTC = local + 5h");
        BitConverter.ToInt32(blob, biases + 4).Should().Be(0, "lStandardBias");
        BitConverter.ToInt32(blob, biases + 8).Should().Be(-60, "lDaylightBias");

        var standard = biases + 12;
        BitConverter.ToUInt16(blob, standard + 2).Should().Be(11, "standard starts in November");
        BitConverter.ToUInt16(blob, standard + 4).Should().Be((ushort)DayOfWeek.Sunday);
        BitConverter.ToUInt16(blob, standard + 6).Should().Be(1, "first Sunday");
        BitConverter.ToUInt16(blob, standard + 8).Should().Be(2, "at 02:00");
        var daylight = standard + 16;
        BitConverter.ToUInt16(blob, daylight + 2).Should().Be(3, "daylight starts in March");
        BitConverter.ToUInt16(blob, daylight + 6).Should().Be(2, "second Sunday");

        blob.Length.Should().Be(rule + 4 + 0x3E, "one rule, nothing after it");

        // What the reader makes of it is the key the expander maps to IANA.
        MapiCalendarOperations.TimeZoneKeyName(blob).Should().Be("Eastern Standard Time");
    }

    [Fact]
    public void Utc_HasZeroTransitionDates()
    {
        var blob = TimeZoneDefinition.Encode(TimeZoneInfo.Utc, Summer);
        MapiCalendarOperations.TimeZoneKeyName(blob).Should().Be(TimeZoneInfo.Utc.Id);
        var rule = 8 + TimeZoneInfo.Utc.Id.Length * 2 + 2;
        var biases = rule + 8 + 14;
        BitConverter.ToInt32(blob, biases).Should().Be(0);
        BitConverter.ToInt32(blob, biases + 8).Should().Be(0);
        blob.Skip(biases + 12).Take(32).Should().AllBeEquivalentTo((byte)0, "no transitions");
    }

    [Fact]
    public void Struct_Is48BytesWithTheSameRule()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var blob = TimeZoneDefinition.EncodeStruct(zone, Summer);
        blob.Should().HaveCount(48);
        BitConverter.ToInt32(blob, 0).Should().Be(300);
        BitConverter.ToInt32(blob, 8).Should().Be(-60);
        BitConverter.ToUInt16(blob, 12).Should().Be(0, "wStandardYear");
        BitConverter.ToUInt16(blob, 14 + 2).Should().Be(11, "standard month");
        BitConverter.ToUInt16(blob, 30).Should().Be(0, "wDaylightYear");
        BitConverter.ToUInt16(blob, 32 + 2).Should().Be(3, "daylight month");
    }

    [Fact]
    public void Resolve_AcceptsIanaWindowsAndFallsBackToLocal()
    {
        TimeZoneDefinition.Resolve("America/New_York").Id.Should().Be("Eastern Standard Time");
        TimeZoneDefinition.Resolve("Eastern Standard Time").Id.Should().Be("Eastern Standard Time");
        TimeZoneDefinition.Resolve(null).Id.Should().Be(TimeZoneInfo.Local.Id);
        TimeZoneDefinition.Resolve("Not/AZone").Id.Should().Be(TimeZoneInfo.Local.Id);
    }

    [Fact]
    public void AppointmentProperties_WriteOnlyResolvedZoneTags()
    {
        var tags = new MapiCalendarTags(
            0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
            0x82180003, 0x82170003, 0x825E0102,
            0x8503000B, 0x85010003, 0x80030102,
            0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
            0x82010003, 0x82200040, 0x82240003, 0x8230001F,
            0, 0, 0,
            TimeZoneDefinitionEnd: 0x825F0102, TimeZoneStruct: 0x82330102, TimeZoneDescription: 0x8234001F);
        var write = new MapiCalendarOperations.AppointmentWrite { StartUtc = Summer, EndUtc = Summer.AddHours(1), TimeZoneId = "America/New_York" };

        var values = MapiCalendarOperations.TimeZoneProperties(tags, write);

        values.Select(v => v.Tag).Should().BeEquivalentTo([0x825E0102u, 0x825F0102u, 0x82330102u, 0x8234001Fu], "recur tag unresolved, so not written");
        MapiCalendarOperations.TimeZoneKeyName((byte[])values[0].Value!).Should().Be("Eastern Standard Time");
        ((byte[])values[2].Value!).Should().HaveCount(48);
        ((string)values[3].Value!).Should().NotBeNullOrEmpty();

        MapiCalendarOperations.Properties(tags, write, includeClass: true).Should().Contain(v => v.Tag == 0x825E0102u);
    }
}
