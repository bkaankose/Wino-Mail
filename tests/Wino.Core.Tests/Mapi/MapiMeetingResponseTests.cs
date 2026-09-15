using Wino.Mapi;
using Wino.Mapi.Rops;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Rung 8, meeting responses (MS-OXOCAL 2.2.7 / 3.1.4.8): the appointment stamps and the response message.</summary>
public class MapiMeetingResponseTests
{
    private static readonly MapiCalendarTags Tags = new(
        0x820D0040, 0x820E0040, 0x8208001F, 0x8215000B, 0x82050003, 0x8223000B, 0x82160102,
        0x82180003, 0x82170003, 0x825E0102,
        0x8503000B, 0x85010003,
        0x80030102,
        0x80230102, 0x801A0040, 0x80010040, 0x8002001F,
        0x82010003, 0x82200040, 0x82240003, 0x8230001F,
        0);

    [Fact]
    public void Accept_StampsResponseStatusBusyAndReplyTime()
    {
        var now = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);
        var values = MapiCalendarOperations.ResponseAppointmentProperties(Tags, MapiCalendarOperations.MeetingResponse.Accept, 3, "Test User", now);

        values.Should().Contain(v => v.Tag == Tags.ResponseStatus && (uint)v.Value == 3);
        values.Should().Contain(v => v.Tag == Tags.BusyStatus && (uint)v.Value == 3);
        values.Should().Contain(v => v.Tag == Tags.AppointmentReplyTime && (DateTime)v.Value == now);
        values.Should().Contain(v => v.Tag == Tags.AppointmentReplyName && (string)v.Value == "Test User");

        var tentative = MapiCalendarOperations.ResponseAppointmentProperties(Tags, MapiCalendarOperations.MeetingResponse.Tentative, null, "T", now);
        tentative.Should().Contain(v => v.Tag == Tags.BusyStatus && (uint)v.Value == 1);
        var decline = MapiCalendarOperations.ResponseAppointmentProperties(Tags, MapiCalendarOperations.MeetingResponse.Decline, null, "T", now);
        decline.Should().Contain(v => v.Tag == Tags.ResponseStatus && (uint)v.Value == 4);
        decline.Should().NotContain(v => v.Tag == Tags.BusyStatus);
    }

    [Fact]
    public void ResponseMessage_CarriesClassIdentityAndOrganizer()
    {
        var now = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Utc);
        var meeting = new MapiCalendarOperations.MeetingIdentity([1, 2, 3], [1, 2, 4], now.AddDays(-1), 2,
            now.AddDays(1), now.AddDays(1).AddHours(1), "Room 9", "Planning", 2, "boss@example.com", "Boss");

        var message = MapiCalendarOperations.BuildResponseMessage(Tags, MapiCalendarOperations.MeetingResponse.Decline, meeting, "Sorry, clash.", now);

        message.MessageClass.Should().Be("IPM.Schedule.Meeting.Resp.Neg");
        message.Subject.Should().Be("Declined: Planning");
        message.TextBody.Should().Be("Sorry, clash.");
        message.Recipients.Should().ContainSingle().Which.SmtpAddress.Should().Be("boss@example.com");
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.GlobalObjectId);
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.CleanGlobalObjectId);
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.OwnerCriticalChange);
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.AttendeeCriticalChange && (DateTime)v.Value == now);
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.AppointmentSequence && (uint)v.Value == 2);
        message.ExtraProperties.Should().Contain(v => v.Tag == Tags.Where && (string)v.Value == "Room 9");

        MapiCalendarOperations.BuildResponseMessage(Tags, MapiCalendarOperations.MeetingResponse.Accept, meeting, null, now).MessageClass.Should().Be("IPM.Schedule.Meeting.Resp.Pos");
        MapiCalendarOperations.BuildResponseMessage(Tags, MapiCalendarOperations.MeetingResponse.Tentative, meeting, "", now).TextBody.Should().BeNull();
    }
}
