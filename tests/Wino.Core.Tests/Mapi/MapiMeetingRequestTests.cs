using Wino.Mapi;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The organizer side (MS-OXOCAL 3.1.4.4 to 3.1.4.6): global object ids, the organizer's stamps and recipient table, the request and the cancellation.</summary>
public class MapiMeetingRequestTests
{
    private static readonly MapiCalendarTags Tags = new(
        0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
        0x82180003, 0x82170003, 0x825E0102,
        0x8503000B, 0x85010003,
        0x80030102,
        0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
        0x82010003, 0x82200040, 0x82240003, 0x8230001F,
        0,
        0x8229000B, 0x82130003);

    /// <summary>A recipient row as RopOpenMessage returns it: SMTP header address plus the meeting columns.</summary>
    private static OpenRecipient Row(byte type, string smtp, string? name, uint flags, uint trackStatus)
        => new(type, RecipientRow.AddressSmtp | RecipientRow.FlagE | RecipientRow.FlagD | RecipientRow.FlagU, smtp, name,
        [
            PropertyValue.Of(PropertyTags.SmtpAddress, smtp),
            PropertyValue.Of(PropertyTags.RecipientFlags, flags),
            PropertyValue.Of(PropertyTags.RecipientTrackStatus, trackStatus),
        ]);

    private static MapiCalendarOperations.AppointmentWrite Meeting()
    {
        var write = new MapiCalendarOperations.AppointmentWrite
        {
            Subject = "Planning",
            Body = "Agenda",
            Location = "Room 1",
            StartUtc = new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 9, 10, 15, 30, 0, DateTimeKind.Utc),
            BusyStatus = 2,
            ReminderMinutes = 15,
            OrganizerName = "Test User",
            OrganizerAddress = "testing@example.com",
        };
        write.Attendees.Add(new MapiCalendarOperations.MeetingAttendee("Matthew Johnson", "matt@example.com", false));
        write.Attendees.Add(new MapiCalendarOperations.MeetingAttendee(null, "opt@example.com", true));
        return write;
    }

    [Fact]
    public void GlobalObjectId_HasByteArrayIdZeroDateCreationTimeAndCountedData()
    {
        var created = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var data = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var id = GlobalObjectId.Create(created, data);

        id.Should().HaveCount(GlobalObjectId.CreatedLength);
        id.Take(16).Should().Equal(GlobalObjectId.ByteArrayId);
        id.Skip(16).Take(4).Should().AllBeEquivalentTo((byte)0, "not an exception: YH YL M D are zero");
        BitConverter.ToInt64(id, 20).Should().Be(created.ToFileTimeUtc());
        id.Skip(28).Take(8).Should().AllBeEquivalentTo((byte)0, "reserved");
        BitConverter.ToUInt32(id, 36).Should().Be(16, "data size");
        id.Skip(40).Should().Equal(data);

        GlobalObjectId.IsWellFormed(id).Should().BeTrue();
        GlobalObjectId.IsWellFormed(null).Should().BeFalse();
        GlobalObjectId.IsWellFormed(new byte[40]).Should().BeFalse();
        GlobalObjectId.Create(created).Skip(40).Should().HaveCount(16, "random data by default");
    }

    [Fact]
    public void MeetingRecipients_OrganizerRowThenAttendeesWithMeetingColumns()
    {
        var rows = MapiCalendarOperations.MeetingRecipients(Meeting(), includeOrganizer: true);

        rows.Should().HaveCount(3);
        rows[0].Type.Should().Be(RopMessageWrite.RecipientType.To);
        rows[0].SmtpAddress.Should().Be("testing@example.com");
        rows[0].Flags.Should().Be(0x3, "sendable organizer");
        rows[1].Type.Should().Be(RopMessageWrite.RecipientType.To);
        rows[1].DisplayName.Should().Be("Matthew Johnson");
        rows[1].Flags.Should().Be(0x1);
        rows[2].Type.Should().Be(RopMessageWrite.RecipientType.Cc, "optional attendee");
        rows[2].DisplayName.Should().Be("opt@example.com", "no name falls back to the address");

        MapiCalendarOperations.MeetingRecipients(Meeting(), includeOrganizer: false).Should().HaveCount(2);

        // The wire form carries the two extra columns for every row.
        var rop = RopMessageWrite.BuildModifyRecipients(1, rows);
        var reader = new RopReader(rop);
        reader.UInt8(); reader.UInt8(); reader.UInt8();
        var columnCount = reader.UInt16();
        columnCount.Should().Be((ushort)RopMessageWrite.MeetingRecipientColumns.Length);
        for (var i = 0; i < columnCount; i++) reader.UInt32().Should().Be(RopMessageWrite.MeetingRecipientColumns[i]);
        reader.UInt16().Should().Be(3, "RowCount");

        reader.UInt32().Should().Be(0);
        reader.UInt8().Should().Be((byte)RopMessageWrite.RecipientType.To);
        var rowSize = reader.UInt16();
        var rowStart = reader.Position;
        reader.UInt16().Should().Be(0x021B);
        reader.UnicodeZ().Should().Be("testing@example.com");
        reader.UnicodeZ().Should().Be("Test User");
        reader.UInt16().Should().Be(columnCount);
        reader.UInt8().Should().Be(0x00);
        reader.UnicodeZ(); reader.UnicodeZ(); reader.UnicodeZ(); reader.UnicodeZ();
        reader.UInt32().Should().Be(0x3, "PidTagRecipientFlags");
        reader.UInt32().Should().Be(0, "PidTagRecipientTrackStatus");
        (reader.Position - rowStart).Should().Be(rowSize);
    }

    [Fact]
    public void PlainRecipients_KeepTheFourStandardColumns()
    {
        var rop = RopMessageWrite.BuildModifyRecipients(1, [new RopMessageWrite.Recipient(RopMessageWrite.RecipientType.To, "A", "a@example.com")]);
        var reader = new RopReader(rop);
        reader.UInt8(); reader.UInt8(); reader.UInt8();
        reader.UInt16().Should().Be((ushort)RopMessageWrite.RecipientColumnsUsed.Length);
    }

    [Fact]
    public void OrganizerProperties_StampMeetingStateIdsAndSequence()
    {
        var now = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);
        var goid = GlobalObjectId.Create(now);
        var values = MapiCalendarOperations.OrganizerProperties(Tags, goid, 2, now, 90);

        values.Should().Contain(v => v.Tag == Tags.StateFlags && (uint)v.Value == 0x1, "asfMeeting, not received");
        values.Should().Contain(v => v.Tag == Tags.ResponseStatus && (uint)v.Value == 1, "respOrganized");
        values.Should().Contain(v => v.Tag == Tags.GlobalObjectId && ((byte[])v.Value).SequenceEqual(goid));
        values.Should().Contain(v => v.Tag == Tags.CleanGlobalObjectId && ((byte[])v.Value).SequenceEqual(goid), "no exception date: clean id equals the id");
        values.Should().Contain(v => v.Tag == Tags.OwnerCriticalChange && (DateTime)v.Value == now);
        values.Should().Contain(v => v.Tag == Tags.AttendeeCriticalChange && (DateTime)v.Value == now);
        values.Should().Contain(v => v.Tag == Tags.AppointmentSequence && (uint)v.Value == 2);
        values.Should().Contain(v => v.Tag == PropertyTags.ResponseRequested && (bool)v.Value);
        values.Should().Contain(v => v.Tag == Tags.FInvited && (bool)v.Value);
        values.Should().Contain(v => v.Tag == Tags.AppointmentDuration && (uint)v.Value == 90);

        var withoutOptional = MapiCalendarOperations.OrganizerProperties(Tags with { FInvited = 0, AppointmentDuration = 0 }, goid, 0, now, 90);
        withoutOptional.Should().NotContain(v => v.Tag == Tags.FInvited || v.Tag == Tags.AppointmentDuration, "unresolved optional tags are skipped");
    }

    [Fact]
    public void MeetingRequest_CarriesClassWhenWhereIdentityAndAttendeesOnly()
    {
        var now = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);
        var meeting = Meeting();
        var goid = GlobalObjectId.Create(now);
        var message = MapiCalendarOperations.BuildMeetingRequest(Tags, meeting, goid, 1, now);

        message.MessageClass.Should().Be("IPM.Schedule.Meeting.Request");
        message.Subject.Should().Be("Planning");
        message.TextBody.Should().Be("Agenda");
        message.HtmlBody.Should().BeNull();
        message.Recipients.Should().HaveCount(2, "the organizer is not a recipient of their own request");
        message.Recipients.Should().OnlyContain(r => r.Flags == null && r.TrackStatus == null, "a transport message carries the plain recipient columns");
        message.Recipients[1].Type.Should().Be(RopMessageWrite.RecipientType.Cc);

        var extra = message.ExtraProperties;
        extra.Should().Contain(v => v.Tag == Tags.GlobalObjectId && ((byte[])v.Value).SequenceEqual(goid));
        extra.Should().Contain(v => v.Tag == Tags.CleanGlobalObjectId);
        extra.Should().Contain(v => v.Tag == Tags.AppointmentSequence && (uint)v.Value == 1);
        extra.Should().Contain(v => v.Tag == Tags.OwnerCriticalChange && (DateTime)v.Value == now);
        extra.Should().Contain(v => v.Tag == Tags.StartWhole && (DateTime)v.Value == meeting.StartUtc);
        extra.Should().Contain(v => v.Tag == Tags.EndWhole && (DateTime)v.Value == meeting.EndUtc);
        extra.Should().Contain(v => v.Tag == Tags.Location && (string)v.Value == "Room 1");
        extra.Should().Contain(v => v.Tag == Tags.Where && (string)v.Value == "Room 1");
        extra.Should().Contain(v => v.Tag == Tags.StateFlags && (uint)v.Value == 0x1);
        extra.Should().Contain(v => v.Tag == Tags.BusyStatus && (uint)v.Value == 1, "tentative until the attendee answers");
        extra.Should().Contain(v => v.Tag == Tags.IntendedBusyStatus && (uint)v.Value == 2);
        extra.Should().Contain(v => v.Tag == PropertyTags.ResponseRequested && (bool)v.Value);
        extra.Should().Contain(v => v.Tag == Tags.ReminderSet && (bool)v.Value);
        extra.Should().Contain(v => v.Tag == Tags.ReminderDelta && (uint)v.Value == 15);
        extra.Should().Contain(v => v.Tag == Tags.AppointmentDuration && (uint)v.Value == 90);

        meeting.Body = "<p>Agenda</p>";
        var html = MapiCalendarOperations.BuildMeetingRequest(Tags, meeting, goid, 0, now);
        html.HtmlBody.Should().Be("<p>Agenda</p>");
        html.TextBody.Should().BeNull();
    }

    [Fact]
    public void Body_EmptyEditorShellIsNothing_HtmlGoesToHtml_PlainToBody()
    {
        MapiCalendarOperations.NormalizeBody(null).Should().Be((null, null));
        MapiCalendarOperations.NormalizeBody("<div><br></div>").Should().Be((null, null), "the editor's empty shell");
        MapiCalendarOperations.NormalizeBody("<p>&nbsp;</p>" + Environment.NewLine).Should().Be((null, null));
        MapiCalendarOperations.NormalizeBody("Plain notes").Should().Be(("Plain notes", null));
        MapiCalendarOperations.NormalizeBody("<div>Bring <b>the</b> deck &amp; pens</div>").Should().Be(("Bring the deck & pens", "<div>Bring <b>the</b> deck &amp; pens</div>"));

        MapiCalendarOperations.BodyProperties("<div><br></div>").Should().ContainSingle(v => v.Tag == PropertyTags.Body && (string)v.Value == "");
        MapiCalendarOperations.BodyProperties("Plain").Should().ContainSingle(v => v.Tag == PropertyTags.Body && (string)v.Value == "Plain");
        var html = MapiCalendarOperations.BodyProperties("<p>Agenda</p>");
        html.Should().ContainSingle(v => v.Tag == PropertyTags.Html);
        System.Text.Encoding.UTF8.GetString((byte[])html[0].Value!).Should().Be("<p>Agenda</p>");
        MapiCalendarOperations.BodyProperties("<p>" + new string('x', 5000) + "</p>").Should().ContainSingle(v => v.Tag == PropertyTags.Body, "too big to inline: text fallback");

        var meeting = Meeting();
        meeting.Body = "<div><br></div>";
        var request = MapiCalendarOperations.BuildMeetingRequest(Tags, meeting, GlobalObjectId.Create(DateTime.UtcNow), 0, DateTime.UtcNow);
        request.HtmlBody.Should().BeNull();
        request.TextBody.Should().BeNull();
    }

    [Fact]
    public void Cancellation_GoesToAttendeesWithCancelledStateAndBumpedSequence()
    {
        var now = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);
        var goid = GlobalObjectId.Create(now);
        var identity = new MapiCalendarOperations.MeetingIdentity(goid, goid, now.AddDays(-1), 2,
            new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc), "Room 1", "Planning", 2,
            "testing@example.com", "Test User",
            [
                Row(OpenRecipient.TypeTo, "testing@example.com", "Test User", flags: 0x3, trackStatus: 0),
                Row(OpenRecipient.TypeTo, "matt@example.com", "Matthew Johnson", flags: 0x1, trackStatus: 3),
                Row(OpenRecipient.TypeCc, "opt@example.com", null, flags: 0x1, trackStatus: 0),
            ]);

        var message = MapiCalendarOperations.BuildMeetingCancellation(Tags, identity, 3, now);

        message.MessageClass.Should().Be("IPM.Schedule.Meeting.Canceled");
        message.Subject.Should().Be("Canceled: Planning");
        message.Recipients.Should().HaveCount(2);
        message.Recipients[0].SmtpAddress.Should().Be("matt@example.com");
        message.Recipients[1].Type.Should().Be(RopMessageWrite.RecipientType.Cc);

        var extra = message.ExtraProperties;
        extra.Should().Contain(v => v.Tag == Tags.GlobalObjectId);
        extra.Should().Contain(v => v.Tag == Tags.AppointmentSequence && (uint)v.Value == 3);
        extra.Should().Contain(v => v.Tag == Tags.StateFlags && (uint)v.Value == 0x5, "meeting + cancelled");
        extra.Should().Contain(v => v.Tag == Tags.BusyStatus && (uint)v.Value == 0, "free");
        extra.Should().Contain(v => v.Tag == Tags.OwnerCriticalChange && (DateTime)v.Value == now);
        extra.Should().Contain(v => v.Tag == Tags.StartWhole);
        extra.Should().Contain(v => v.Tag == Tags.Where && (string)v.Value == "Room 1");
    }
}
