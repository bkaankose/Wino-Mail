using Wino.Mapi;
using Wino.Mapi.Rops;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>The text/calendar part synthesized for meeting messages (MS-OXCICAL shape).</summary>
public class MapiInvitationTests
{
    [Fact]
    public void ICalendar_RequestCarriesUidTimesOrganizerAndAttendees()
    {
        var now = new DateTime(2026, 9, 4, 22, 0, 0, DateTimeKind.Utc);
        var attendees = new List<OpenRecipient>
        {
            new(0x01, 0x0651, "/o=Org/cn=boss", "Boss", [PropertyValue.Of(PropertyTags.SmtpAddress, "boss@example.com"), PropertyValue.Of(PropertyTags.RecipientFlags, 3u)]),
            new(0x01, 0x021B, "me@example.com", "Me", [PropertyValue.Of(PropertyTags.SmtpAddress, "me@example.com"), PropertyValue.Of(PropertyTags.RecipientTrackStatus, 5u)]),
            new(0x02, 0x021B, "opt@example.com", "Opt, Person", [PropertyValue.Of(PropertyTags.SmtpAddress, "opt@example.com"), PropertyValue.Of(PropertyTags.RecipientTrackStatus, 3u)]),
        };
        var meeting = new MapiCalendarOperations.MeetingIdentity([0xAB, 0xCD], [0xAB, 0xCE], null, 1,
            new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 10, 14, 0, 0, DateTimeKind.Utc),
            "Room 1; Floor 2", "Planning, Q4", null, "boss@example.com", "Boss", attendees);

        var ics = MapiCalendarOperations.BuildICalendar(meeting, "REQUEST", now);

        ics.Should().StartWith("BEGIN:VCALENDAR\r\n");
        ics.Should().Contain("METHOD:REQUEST\r\n");
        ics.Should().Contain("UID:ABCE\r\n");
        ics.Should().Contain("DTSTART:20260910T130000Z\r\n");
        ics.Should().Contain("DTEND:20260910T140000Z\r\n");
        ics.Should().Contain("SUMMARY:Planning\\, Q4\r\n");
        ics.Should().Contain("LOCATION:Room 1\\; Floor 2\r\n");
        ics.Should().Contain("ORGANIZER;CN=\"Boss\":mailto:boss@example.com\r\n");
        ics.Should().Contain("ATTENDEE;CN=\"Me\";ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:me@example.com\r\n");
        ics.Should().Contain("ATTENDEE;CN=\"Opt, Person\";ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:opt@example.com\r\n");
        ics.Should().NotContain("mailto:boss@example.com;");                      // the organizer is not an attendee line
        ics.Should().EndWith("END:VCALENDAR\r\n");

        MapiCalendarOperations.MeetingMethod("IPM.Schedule.Meeting.Request").Should().Be("REQUEST");
        MapiCalendarOperations.MeetingMethod("IPM.Schedule.Meeting.Canceled").Should().Be("CANCEL");
        MapiCalendarOperations.MeetingMethod("IPM.Schedule.Meeting.Resp.Pos").Should().Be("REPLY");
        MapiCalendarOperations.MeetingMethod("IPM.Note").Should().BeNull();
    }

    [Fact]
    public void ICalendar_AllDay_UsesDateValues()
    {
        var meeting = new MapiCalendarOperations.MeetingIdentity(null, null, null, 0,
            new DateTime(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 11, 4, 0, 0, DateTimeKind.Utc),
            null, "Offsite", null, null, null, null, AllDay: true);

        var ics = MapiCalendarOperations.BuildICalendar(meeting, "CANCEL", DateTime.UtcNow);

        ics.Should().Contain("DTSTART;VALUE=DATE:20260910\r\n");
        ics.Should().Contain("DTEND;VALUE=DATE:20260911\r\n");
        ics.Should().Contain("METHOD:CANCEL\r\n");
        ics.Should().NotContain("ORGANIZER");
    }
}
