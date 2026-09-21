using FluentAssertions;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests.Models;

/// <summary>The invitation card's parser over a text/calendar part.</summary>
public class InvitationDetailsTests
{
    [Fact]
    public void Parse_RequestWithFoldedLinesAndParameters()
    {
        const string ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:ABCE\r\n" +
                           "DTSTART:20260907T120000Z\r\nDTEND:20260907T123000Z\r\n" +
                           "SUMMARY:Test\\, Meeting\r\nLOCATION:Room 1\\; Floor 2\r\n" +
                           "ORGANIZER;CN=\"Test User\":mailto:testing@example.com\r\n" +
                           "ATTENDEE;CN=\"Matthew Johnson\";ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;\r\n RSVP=TRUE:mailto:matt@example.com\r\n" +
                           "ATTENDEE;CN=\"Opt: Person\";ROLE=OPT-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:opt@example.com\r\n" +
                           "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var d = InvitationDetails.Parse(ics);

        d.IsRequest.Should().BeTrue();
        d.Uid.Should().Be("ABCE");
        d.Summary.Should().Be("Test, Meeting");
        d.Location.Should().Be("Room 1; Floor 2");
        d.Start.Should().Be(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        d.End.Should().Be(new DateTimeOffset(2026, 9, 7, 12, 30, 0, TimeSpan.Zero));
        d.IsAllDay.Should().BeFalse();
        d.OrganizerName.Should().Be("Test User");
        d.OrganizerEmail.Should().Be("testing@example.com");
        d.Attendees.Should().HaveCount(2);
        d.Attendees[0].Name.Should().Be("Matthew Johnson");
        d.Attendees[0].IsOptional.Should().BeFalse();
        d.Attendees[0].ParticipationStatus.Should().Be("NEEDS-ACTION");
        d.Attendees[1].Name.Should().Be("Opt: Person");                 // the colon inside quotes is not the value separator
        d.Attendees[1].IsOptional.Should().BeTrue();
        d.Attendees[1].ParticipationStatus.Should().Be("ACCEPTED");
    }

    [Fact]
    public void Parse_AllDayCancel_AndGarbage()
    {
        var d = InvitationDetails.Parse("BEGIN:VCALENDAR\nMETHOD:CANCEL\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20260910\nDTEND;VALUE=DATE:20260911\nSUMMARY:Offsite\nEND:VEVENT\nEND:VCALENDAR\n");
        d.IsCancellation.Should().BeTrue();
        d.IsAllDay.Should().BeTrue();
        d.Start!.Value.Date.Should().Be(new DateTime(2026, 9, 10));

        InvitationDetails.Parse(null).Method.Should().BeNull();
        InvitationDetails.Parse("not ics at all").Attendees.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReplyCarriesResponderStatus_AndOnlyFirstEvent()
    {
        var d = InvitationDetails.Parse("BEGIN:VCALENDAR\r\nMETHOD:REPLY\r\nBEGIN:VEVENT\r\nUID:FIRST\r\n" +
                                        "ATTENDEE;PARTSTAT=tentative:mailto:me@example.com\r\nEND:VEVENT\r\n" +
                                        "BEGIN:VEVENT\r\nUID:SECOND\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        d.IsReply.Should().BeTrue();
        d.Uid.Should().Be("FIRST");
        d.Attendees.Should().ContainSingle().Which.ParticipationStatus.Should().Be("TENTATIVE");
    }
}
