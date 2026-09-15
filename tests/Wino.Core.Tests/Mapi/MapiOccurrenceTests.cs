using Wino.Mapi;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Per-occurrence edits (MS-OXOCAL 3.1.4.2 / 3.1.4.3): the blob with deleted dates and exceptions, and the exception attachment.</summary>
public class MapiOccurrenceTests
{
    private static readonly DateTime Start = new(2026, 9, 15, 9, 0, 0);          // Tuesday, wall clock

    private static AppointmentRecurrence Weekly()
        => RecurrenceEncoder.FromRRule("FREQ=WEEKLY;BYDAY=TU;COUNT=6", Start, TimeSpan.FromMinutes(30));

    [Fact]
    public void DeletedOccurrence_JoinsDeletedDates_AndRoundTrips()
    {
        var series = Weekly();
        var updated = RecurrenceEncoder.WithDeletedOccurrence(series, new DateTime(2026, 9, 22, 9, 0, 0));
        updated.DeletedInstanceDates.Should().Equal(new DateTime(2026, 9, 22));
        updated.ModifiedInstanceDates.Should().BeEmpty();

        RecurrenceEncoder.WithDeletedOccurrence(updated, new DateTime(2026, 9, 22, 9, 0, 0)).DeletedInstanceDates.Should().HaveCount(1, "idempotent");

        var back = AppointmentRecurrence.Parse(RecurrenceEncoder.Encode(updated));
        back.DeletedInstanceDates.Should().Equal(new DateTime(2026, 9, 22));
        back.Exceptions.Should().BeEmpty();
        back.OccurrenceCount.Should().Be(6);
    }

    [Fact]
    public void Exception_IsListedAsModifiedAndDeleted_AndRoundTripsWithOverrides()
    {
        var series = Weekly();
        var moved = new RecurrenceException(
            new DateTime(2026, 9, 22, 9, 0, 0), new DateTime(2026, 9, 23, 14, 0, 0), new DateTime(2026, 9, 23, 15, 0, 0),
            "Moved: Planning", "Room 2", 1, null);
        var updated = RecurrenceEncoder.WithException(series, moved);

        updated.ModifiedInstanceDates.Should().Equal(new DateTime(2026, 9, 22));
        updated.DeletedInstanceDates.Should().Equal([new DateTime(2026, 9, 22)], "the vacated slot is listed as deleted too");
        updated.Exceptions.Should().ContainSingle();

        var back = AppointmentRecurrence.Parse(RecurrenceEncoder.Encode(updated));
        back.Exceptions.Should().ContainSingle();
        var e = back.Exceptions[0];
        e.OriginalStart.Should().Be(new DateTime(2026, 9, 22, 9, 0, 0));
        e.Start.Should().Be(new DateTime(2026, 9, 23, 14, 0, 0));
        e.End.Should().Be(new DateTime(2026, 9, 23, 15, 0, 0));
        e.Subject.Should().Be("Moved: Planning", "the Unicode form wins over the ANSI one");
        e.Location.Should().Be("Room 2");
        e.BusyStatus.Should().Be(1);
        e.AllDay.Should().BeNull();

        // A second change to the same occurrence replaces the first; a later delete drops it.
        var again = RecurrenceEncoder.WithException(updated, moved with { Start = moved.Start.AddHours(1), End = moved.End.AddHours(1), Subject = null });
        again.Exceptions.Should().ContainSingle().Which.Start.Should().Be(new DateTime(2026, 9, 23, 15, 0, 0));
        AppointmentRecurrence.Parse(RecurrenceEncoder.Encode(again)).Exceptions[0].Subject.Should().BeNull();

        var dropped = RecurrenceEncoder.WithDeletedOccurrence(again, new DateTime(2026, 9, 22, 9, 0, 0));
        dropped.Exceptions.Should().BeEmpty();
        dropped.ModifiedInstanceDates.Should().BeEmpty();
        dropped.DeletedInstanceDates.Should().Equal(new DateTime(2026, 9, 22));
    }

    [Fact]
    public void TwoExceptions_StayOrderedAndDistinct()
    {
        var series = Weekly();
        var a = new RecurrenceException(new DateTime(2026, 9, 29, 9, 0, 0), new DateTime(2026, 9, 29, 10, 0, 0), new DateTime(2026, 9, 29, 10, 30, 0), null, null, null, null);
        var b = new RecurrenceException(new DateTime(2026, 9, 22, 9, 0, 0), new DateTime(2026, 9, 22, 11, 0, 0), new DateTime(2026, 9, 22, 11, 30, 0), "B", null, null, true);
        var updated = RecurrenceEncoder.WithException(RecurrenceEncoder.WithException(series, a), b);

        var back = AppointmentRecurrence.Parse(RecurrenceEncoder.Encode(updated));
        back.Exceptions.Select(e => e.OriginalStart.Day).Should().Equal(22, 29);
        back.ModifiedInstanceDates.Select(d => d.Day).Should().Equal(22, 29);
        back.Exceptions[0].Subject.Should().Be("B");
        back.Exceptions[0].AllDay.Should().BeTrue();
        back.Exceptions[1].Subject.Should().BeNull();
        back.Exceptions[1].Start.Should().Be(a.Start);
    }

    [Fact]
    public void EmbeddedMessageAndDeleteAttachmentRops_Encode()
    {
        var open = RopMessageWrite.BuildOpenEmbeddedMessage(3, 4, create: true);
        open.Should().Equal([0x46, 0x00, 0x03, 0x04, 0xFF, 0x0F, 0x02]);
        RopMessageWrite.BuildOpenEmbeddedMessage(3, 4, create: false)[^1].Should().Be(0x01);

        var response = new RopWriter();
        response.UInt8(0x46); response.UInt8(4); response.UInt32(0); response.UInt8(0); response.UInt64(0x1122334455667788);
        RopMessageWrite.ParseOpenEmbeddedMessage(new RopReader(response.ToArray())).Should().Be(0x1122334455667788UL);

        var delete = RopMessageWrite.BuildDeleteAttachment(1, 7);
        delete.Should().Equal([0x24, 0x00, 0x01, 0x07, 0x00, 0x00, 0x00]);
    }

    private static MapiCalendarTags MeetingTags => new(
        0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
        0x82180003, 0x82170003, 0x825E0102,
        0x8503000B, 0x85010003, 0x80030102,
        0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
        0x82010003, 0x82200040, 0x82240003, 0x8230001F,
        0, ExceptionReplaceTime: 0x82280040, IsRecurring: 0x8005000B, IsException: 0x800A000B);

    [Fact]
    public void InstanceId_StampsTheOriginalDate()
    {
        var master = GlobalObjectId.Create(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var instance = GlobalObjectId.ForInstance(master, new DateTime(2026, 9, 22));

        instance.Take(16).Should().Equal(master.Take(16));
        instance[16].Should().Be(0x07); instance[17].Should().Be(0xEA);   // 2026
        instance[18].Should().Be(9); instance[19].Should().Be(22);
        instance.Skip(20).Should().Equal(master.Skip(20));
        master.Skip(16).Take(4).Should().AllBeEquivalentTo((byte)0, "the master is untouched");
    }

    [Fact]
    public void OccurrenceRequestAndCancellation_NameTheInstance()
    {
        var tags = MeetingTags;
        var now = new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc);
        var masterGoid = GlobalObjectId.Create(now.AddDays(-9));
        var clean = (byte[])masterGoid.Clone();
        var master = new MapiCalendarOperations.MeetingIdentity(masterGoid, clean, now.AddDays(-9), 2,
            new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 15, 13, 30, 0, DateTimeKind.Utc), "Room 1", "Planning", 2,
            "testing@example.com", "Test User",
            [
                new OpenRecipient(OpenRecipient.TypeTo, RecipientRow.AddressSmtp | RecipientRow.FlagE | RecipientRow.FlagD | RecipientRow.FlagU, "testing@example.com", "Test User", [PropertyValue.Of(PropertyTags.RecipientFlags, 3u)]),
                new OpenRecipient(OpenRecipient.TypeTo, RecipientRow.AddressSmtp | RecipientRow.FlagE | RecipientRow.FlagD | RecipientRow.FlagU, "matt@example.com", "Matthew Johnson", [PropertyValue.Of(PropertyTags.RecipientFlags, 1u)]),
            ]);
        var original = new DateTime(2026, 9, 22, 13, 0, 0, DateTimeKind.Utc);
        var change = new MapiCalendarOperations.AppointmentWrite
        {
            Subject = "Planning (moved)", StartUtc = new DateTime(2026, 9, 23, 18, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 9, 23, 19, 0, 0, DateTimeKind.Utc),
            BusyStatus = 2, TimeZoneId = "America/New_York",
        };

        var request = MapiCalendarOperations.BuildOccurrenceRequest(tags, master, change, original, new DateTime(2026, 9, 22, 9, 0, 0), 2, now);
        request.MessageClass.Should().Be("IPM.Schedule.Meeting.Request");
        request.Subject.Should().Be("Planning (moved)");
        request.Recipients.Should().ContainSingle(r => r.SmtpAddress == "matt@example.com", "the organizer is not a recipient");
        var goid = (byte[])request.ExtraProperties.First(v => v.Tag == tags.GlobalObjectId).Value!;
        goid[18].Should().Be(9); goid[19].Should().Be(22);
        ((byte[])request.ExtraProperties.First(v => v.Tag == tags.CleanGlobalObjectId).Value!).Should().Equal(clean, "the clean id stays the master's");
        request.ExtraProperties.Should().ContainSingle(v => v.Tag == tags.CleanGlobalObjectId);
        request.ExtraProperties.Should().Contain(v => v.Tag == tags.IsException && (bool)v.Value!);
        request.ExtraProperties.Should().Contain(v => v.Tag == tags.IsRecurring && (bool)v.Value!);
        request.ExtraProperties.Should().Contain(v => v.Tag == tags.ExceptionReplaceTime && (DateTime)v.Value! == original);
        request.ExtraProperties.Should().Contain(v => v.Tag == tags.StartWhole && (DateTime)v.Value! == change.StartUtc);
        request.ExtraProperties.Should().Contain(v => v.Tag == tags.AppointmentSequence && (uint)v.Value! == 2);

        var cancellation = MapiCalendarOperations.BuildOccurrenceCancellation(tags, master, original, new DateTime(2026, 9, 22, 9, 0, 0), 2, now);
        cancellation.MessageClass.Should().Be("IPM.Schedule.Meeting.Canceled");
        cancellation.Subject.Should().Be("Canceled: Planning");
        cancellation.Recipients.Should().ContainSingle(r => r.SmtpAddress == "matt@example.com");
        ((byte[])cancellation.ExtraProperties.First(v => v.Tag == tags.GlobalObjectId).Value!)[19].Should().Be(22);
        cancellation.ExtraProperties.Should().Contain(v => v.Tag == tags.IsException && (bool)v.Value!);
        cancellation.ExtraProperties.Should().Contain(v => v.Tag == tags.StartWhole && (DateTime)v.Value! == original);
    }

    [Fact]
    public void SeriesRewrite_KeepsExceptions_WhenThePatternIsUnchanged()
    {
        var tags = MeetingTags;
        var existing = RecurrenceEncoder.WithDeletedOccurrence(
            RecurrenceEncoder.FromRRule("FREQ=WEEKLY;BYDAY=TU;COUNT=6", Start, TimeSpan.FromMinutes(30)),
            new DateTime(2026, 9, 22, 9, 0, 0));

        var sameDaysNewTime = new MapiCalendarOperations.AppointmentWrite
        {
            StartUtc = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc),   // 10:00 Eastern
            TimeZoneId = "America/New_York", RecurrenceRule = "RRULE:FREQ=WEEKLY;BYDAY=TU;COUNT=6", ExistingRecurrence = existing,
        };
        var kept = MapiCalendarOperations.BuildRecurrence(sameDaysNewTime);
        kept.DeletedInstanceDates.Should().Equal(new DateTime(2026, 9, 22));
        kept.StartTimeOffset.Should().Be(TimeSpan.FromHours(10));

        var otherDays = new MapiCalendarOperations.AppointmentWrite
        {
            StartUtc = sameDaysNewTime.StartUtc, EndUtc = sameDaysNewTime.EndUtc, TimeZoneId = "America/New_York",
            RecurrenceRule = "RRULE:FREQ=WEEKLY;BYDAY=WE;COUNT=6", ExistingRecurrence = existing,
        };
        MapiCalendarOperations.BuildRecurrence(otherDays).DeletedInstanceDates.Should().BeEmpty("a changed pattern starts clean, as Outlook does");
    }

    [Fact]
    public void ExceptionAttachment_AndMessage_CarryTheOccurrence()
    {
        var tags = new MapiCalendarTags(
            0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
            0x82180003, 0x82170003, 0x825E0102,
            0x8503000B, 0x85010003, 0x80030102,
            0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
            0x82010003, 0x82200040, 0x82240003, 0x8230001F,
            0, ExceptionReplaceTime: 0x82280040);
        var original = new DateTime(2026, 9, 22, 13, 0, 0, DateTimeKind.Utc);
        var change = new MapiCalendarOperations.AppointmentWrite
        {
            Subject = "Planning (moved)", Location = "Room 2", Body = "<div><br></div>",
            StartUtc = new DateTime(2026, 9, 23, 18, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 9, 23, 19, 0, 0, DateTimeKind.Utc),
            BusyStatus = 1, ReminderMinutes = 10,
        };

        var attachment = MapiOccurrenceOperations.ExceptionAttachmentProperties(change, original, new DateTime(2026, 9, 23, 14, 0, 0), new DateTime(2026, 9, 23, 15, 0, 0));
        attachment.Should().Contain(v => v.Tag == PropertyTags.AttachMethod && (uint)v.Value! == 5, "embedded message");
        attachment.Should().Contain(v => v.Tag == PropertyTags.AttachmentHidden && (bool)v.Value!);
        attachment.Should().Contain(v => v.Tag == PropertyTags.AttachmentFlags && (uint)v.Value! == 2, "afException");
        attachment.Should().Contain(v => v.Tag == PropertyTags.RenderingPosition && (uint)v.Value! == 0xFFFFFFFF);
        attachment.Should().Contain(v => v.Tag == PropertyTags.ExceptionReplaceTime && (DateTime)v.Value! == original);
        attachment.Should().Contain(v => v.Tag == PropertyTags.ExceptionStartTime && ((DateTime)v.Value!).Hour == 14, "wall clock");

        var message = MapiOccurrenceOperations.ExceptionMessageProperties(tags, change, original);
        message.Should().Contain(v => v.Tag == PropertyTags.MessageClass && (string)v.Value! == PropertyTags.ExceptionMessageClass);
        message.Should().Contain(v => v.Tag == tags.StartWhole && (DateTime)v.Value! == change.StartUtc);
        message.Should().Contain(v => v.Tag == tags.ExceptionReplaceTime && (DateTime)v.Value! == original);
        message.Should().Contain(v => v.Tag == tags.Recurring && !(bool)v.Value!);
        message.Should().Contain(v => v.Tag == tags.ReminderDelta && (uint)v.Value! == 10);
        message.Should().Contain(v => v.Tag == PropertyTags.Body && (string)v.Value! == "", "the editor's empty shell is nothing");
    }
}
