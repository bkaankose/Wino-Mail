using FluentAssertions;
using Ical.Net.CalendarComponents;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// RFC 6638 write rules: the user's identity comes from the calendar-user-address-set, an attendee
/// answers with its own PARTSTAT only, and an organizer edit never rewrites other attendees' answers.
/// </summary>
public sealed class CalDavSchedulingMutatorTests
{
    private static readonly CalDavUserIdentity Me = CalDavUserIdentity.Resolve(
        ["/123/principal/", "urn:uuid:me-1234", "mailto:me@example.test", "mailto:Alias@Example.test", "mailto:null"],
        "me@example.test");

    private const string InvitationIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Server//Tests//EN
        BEGIN:VEVENT
        UID:invite-1
        DTSTAMP:20260201T000000Z
        DTSTART:20260219T100000Z
        DTEND:20260219T110000Z
        SUMMARY:Planning
        LOCATION:Room 4
        STATUS:CONFIRMED
        SEQUENCE:3
        X-SERVER-PROPERTY:keep-me
        ORGANIZER;CN=Boss;SCHEDULE-STATUS=1.2:mailto:boss@other.test
        ATTENDEE;CN=Boss;ROLE=CHAIR;PARTSTAT=ACCEPTED:mailto:boss@other.test
        ATTENDEE;CN=Me;RSVP=TRUE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:ME@EXAMPLE.TEST
        ATTENDEE;CN=Colleague;ROLE=OPT-PARTICIPANT;PARTSTAT=TENTATIVE:mailto:colleague@other.test
        END:VEVENT
        END:VCALENDAR
        """;

    private const string RecurringInvitationIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Server//Tests//EN
        BEGIN:VEVENT
        UID:series-1
        DTSTAMP:20260201T000000Z
        DTSTART:20260219T100000Z
        DTEND:20260219T110000Z
        RRULE:FREQ=DAILY;COUNT=5
        SUMMARY:Daily
        SEQUENCE:1
        ORGANIZER:mailto:boss@other.test
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:boss@other.test
        ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:alias@example.test
        END:VEVENT
        BEGIN:VEVENT
        UID:series-1
        RECURRENCE-ID:20260221T100000Z
        DTSTAMP:20260201T000000Z
        DTSTART:20260221T120000Z
        DTEND:20260221T130000Z
        SUMMARY:Daily moved
        SEQUENCE:1
        ORGANIZER:mailto:boss@other.test
        ATTENDEE;PARTSTAT=ACCEPTED:mailto:boss@other.test
        ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:alias@example.test
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Resolve_PrefersTheAccountMailto_AndIgnoresNonMailAddresses()
    {
        Me.OrganizerAddress.Should().Be("me@example.test");
        Me.EmailAddresses.Should().BeEquivalentTo(["me@example.test", "Alias@Example.test"]);

        Me.IsUser("mailto:ALIAS@example.TEST").Should().BeTrue();
        Me.IsUser("me@EXAMPLE.test").Should().BeTrue();
        Me.IsUser("urn:uuid:ME-1234").Should().BeTrue();
        Me.IsUser("/123/principal/").Should().BeTrue();
        Me.IsUser("mailto:null").Should().BeFalse();
        Me.IsUser("mailto:boss@other.test").Should().BeFalse();
    }

    [Fact]
    public void Resolve_WithoutTheAccountInTheSet_UsesTheFirstMailto_NeverNullOrPrincipal()
    {
        var identity = CalDavUserIdentity.Resolve(
            ["/1/principal/", "urn:uuid:x", "mailto:null", "mailto:first@icloud.test", "mailto:second@outlook.test"],
            "login@me.test");

        identity.OrganizerAddress.Should().Be("first@icloud.test");
        identity.IsUser("mailto:login@me.test").Should().BeTrue("the account address always identifies the user");
        identity.ResolveOrganizerAddress("login@me.test").Should().Be("first@icloud.test");
        identity.ResolveOrganizerAddress(string.Empty).Should().Be("first@icloud.test");
        identity.ResolveOrganizerAddress("boss@other.test").Should().Be("boss@other.test");
    }

    [Fact]
    public void Resolve_YahooStyleBrokenSet_FallsBackToTheAccountAddress()
    {
        var identity = CalDavUserIdentity.Resolve(["mailto:null"], "user@yahoo.test");

        identity.OrganizerAddress.Should().Be("user@yahoo.test");
        identity.IsUser("mailto:null").Should().BeFalse();
    }

    [Fact]
    public void IsOrganizedByOther_DistinguishesAttendeeFromOrganizer()
    {
        CalDavIcsMutator.IsOrganizedByOther(InvitationIcs, "invite-1", Me).Should().BeTrue();

        var mine = InvitationIcs.Replace("ORGANIZER;CN=Boss;SCHEDULE-STATUS=1.2:mailto:boss@other.test", "ORGANIZER:mailto:alias@example.test");
        CalDavIcsMutator.IsOrganizedByOther(mine, "invite-1", Me).Should().BeFalse();

        var noOrganizer = InvitationIcs.Replace("ORGANIZER;CN=Boss;SCHEDULE-STATUS=1.2:mailto:boss@other.test\n", string.Empty);
        CalDavIcsMutator.IsOrganizedByOther(noOrganizer, "invite-1", Me).Should().BeFalse();

        // An occurrence without its own exception follows the master.
        CalDavIcsMutator.IsOrganizedByOther(RecurringInvitationIcs, "series-1::20260220T100000Z", Me).Should().BeTrue();
    }

    [Fact]
    public void SetOwnParticipation_ChangesOnlyTheUsersPartStat()
    {
        var result = CalDavIcsMutator.SetOwnParticipation(InvitationIcs, "invite-1", Me, AttendeeStatus.Accepted);
        var calendarEvent = IcalCalendar.Load(result).Events.Single();

        Attendee("me@example.test", calendarEvent).ParticipationStatus.Should().Be("ACCEPTED");
        Attendee("me@example.test", calendarEvent).Rsvp.Should().BeTrue("the line keeps its other parameters");
        Attendee("me@example.test", calendarEvent).CommonName.Should().Be("Me");
        Attendee("boss@other.test", calendarEvent).ParticipationStatus.Should().Be("ACCEPTED");
        Attendee("colleague@other.test", calendarEvent).ParticipationStatus.Should().Be("TENTATIVE");
        calendarEvent.Attendees.Should().HaveCount(3);

        calendarEvent.Sequence.Should().Be(3, "a reply never bumps SEQUENCE");
        calendarEvent.Status.Should().Be("CONFIRMED", "a reply never touches STATUS");
        calendarEvent.Summary.Should().Be("Planning");
        calendarEvent.Location.Should().Be("Room 4");
        calendarEvent.Properties["X-SERVER-PROPERTY"].Value.Should().Be("keep-me");
        calendarEvent.Organizer.Value.OriginalString.Should().Be("mailto:boss@other.test");
        calendarEvent.Organizer.Parameters.Get("SCHEDULE-STATUS").Should().Be("1.2");
        calendarEvent.DtStamp.AsUtc.Should().BeAfter(new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(AttendeeStatus.Tentative, "TENTATIVE")]
    [InlineData(AttendeeStatus.Declined, "DECLINED")]
    public void SetOwnParticipation_WritesEachAnswer(AttendeeStatus status, string expected)
    {
        var result = CalDavIcsMutator.SetOwnParticipation(InvitationIcs, "invite-1", Me, status);

        Attendee("me@example.test", IcalCalendar.Load(result).Events.Single()).ParticipationStatus.Should().Be(expected);
        IcalCalendar.Load(result).Events.Single().Status.Should().Be("CONFIRMED");
    }

    [Fact]
    public void SetOwnParticipation_UserMissingFromTheServerCopy_AddsTheLine()
    {
        var withoutMe = InvitationIcs.Replace("ATTENDEE;CN=Me;RSVP=TRUE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:ME@EXAMPLE.TEST\n", string.Empty);

        var result = CalDavIcsMutator.SetOwnParticipation(withoutMe, "invite-1", Me, AttendeeStatus.Tentative);
        var calendarEvent = IcalCalendar.Load(result).Events.Single();

        calendarEvent.Attendees.Should().HaveCount(3);
        Attendee("me@example.test", calendarEvent).ParticipationStatus.Should().Be("TENTATIVE");
        calendarEvent.Sequence.Should().Be(3);
    }

    [Fact]
    public void SetOwnParticipation_Series_AnswersMasterAndEveryExceptionWithTheUser()
    {
        var result = CalDavIcsMutator.SetOwnParticipation(RecurringInvitationIcs, "series-1", Me, AttendeeStatus.Accepted);
        var events = IcalCalendar.Load(result).Events.ToList();

        events.Should().HaveCount(2);
        events.Should().OnlyContain(value => Attendee("alias@example.test", value).ParticipationStatus == "ACCEPTED");
        events.Should().OnlyContain(value => value.Sequence == 1);
        events.Single(value => value.RecurrenceIdentifier == null).RecurrenceRule.Should().NotBeNull();
    }

    [Fact]
    public void SetOwnParticipation_ExistingException_AnswersOnlyThatInstance()
    {
        var result = CalDavIcsMutator.SetOwnParticipation(RecurringInvitationIcs, "series-1::20260221T100000Z", Me, AttendeeStatus.Declined);
        var events = IcalCalendar.Load(result).Events.ToList();

        events.Should().HaveCount(2);
        Attendee("alias@example.test", events.Single(value => value.RecurrenceIdentifier != null)).ParticipationStatus.Should().Be("DECLINED");
        Attendee("alias@example.test", events.Single(value => value.RecurrenceIdentifier == null)).ParticipationStatus.Should().Be("NEEDS-ACTION");
        events.Single(value => value.RecurrenceIdentifier != null).Summary.Should().Be("Daily moved");
    }

    [Fact]
    public void SetOwnParticipation_OccurrenceWithoutException_AddsAnExceptionForThatInstance()
    {
        var result = CalDavIcsMutator.SetOwnParticipation(RecurringInvitationIcs, "series-1::20260220T100000Z", Me, AttendeeStatus.Tentative);
        var events = IcalCalendar.Load(result).Events.ToList();

        events.Should().HaveCount(3);
        var master = events.Single(value => value.RecurrenceIdentifier == null);
        Attendee("alias@example.test", master).ParticipationStatus.Should().Be("NEEDS-ACTION");
        master.RecurrenceRule.Should().NotBeNull();

        var added = events.Single(value => value.RecurrenceIdentifier?.StartTime.AsUtc == new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc));
        added.Start.AsUtc.Should().Be(new DateTime(2026, 2, 20, 10, 0, 0, DateTimeKind.Utc));
        added.End.AsUtc.Should().Be(new DateTime(2026, 2, 20, 11, 0, 0, DateTimeKind.Utc));
        added.RecurrenceRule.Should().BeNull();
        added.Sequence.Should().Be(1);
        added.Organizer.Value.OriginalString.Should().Be("mailto:boss@other.test");
        Attendee("alias@example.test", added).ParticipationStatus.Should().Be("TENTATIVE");
    }

    [Fact]
    public void UpdateEvent_OrganizerEdit_KeepsEveryAttendeesAnswerAndBumpsSequence()
    {
        var mine = InvitationIcs
            .Replace("ORGANIZER;CN=Boss;SCHEDULE-STATUS=1.2:mailto:boss@other.test", "ORGANIZER;CN=Me:mailto:me@example.test")
            .Replace("ATTENDEE;CN=Me;RSVP=TRUE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:ME@EXAMPLE.TEST\n", string.Empty);
        var item = CreateItem("invite-1", organizerEmail: "me@example.test");

        // Local rows lag behind the server: both still say NEEDS-ACTION.
        var attendees = new List<CalendarEventAttendee>
        {
            new() { Email = "boss@other.test", Name = "Boss", AttendenceStatus = AttendeeStatus.NeedsAction },
            new() { Email = "Colleague@Other.test", Name = "Colleague", AttendenceStatus = AttendeeStatus.NeedsAction, IsOptionalAttendee = true },
            new() { Email = "new@other.test", Name = "Newcomer", AttendenceStatus = AttendeeStatus.NeedsAction }
        };

        var result = CalDavIcsMutator.UpdateEvent(mine, item, attendees, Me);
        var calendarEvent = IcalCalendar.Load(result).Events.Single();

        calendarEvent.Attendees.Should().HaveCount(3, "every stored line is replaced, not only the first");
        calendarEvent.Sequence.Should().Be(4, "organizer edits still bump SEQUENCE so the server sends updates");
        calendarEvent.Summary.Should().Be("Edited");
        Attendee("boss@other.test", calendarEvent).ParticipationStatus.Should().Be("ACCEPTED");
        Attendee("boss@other.test", calendarEvent).Role.Should().Be("CHAIR");
        Attendee("colleague@other.test", calendarEvent).ParticipationStatus.Should().Be("TENTATIVE");
        Attendee("new@other.test", calendarEvent).ParticipationStatus.Should().Be("NEEDS-ACTION");
        calendarEvent.Organizer.Value.OriginalString.Should().Be("mailto:me@example.test");
    }

    [Fact]
    public void UpdateEvent_UserOrganizedEventWithoutOrganizer_GetsTheAddressSetIdentity()
    {
        var noOrganizer = InvitationIcs.Replace("ORGANIZER;CN=Boss;SCHEDULE-STATUS=1.2:mailto:boss@other.test\n", string.Empty);
        var identity = CalDavUserIdentity.Resolve(["mailto:null", "urn:uuid:1", "mailto:calendar@icloud.test"], "login@me.test");
        var item = CreateItem("invite-1", organizerEmail: "login@me.test");

        var result = CalDavIcsMutator.UpdateEvent(noOrganizer, item, [new CalendarEventAttendee { Email = "boss@other.test" }], identity);

        IcalCalendar.Load(result).Events.Single().Organizer.Value.OriginalString.Should().Be("mailto:calendar@icloud.test");
    }

    [Fact]
    public void UpdateEvent_AttendeeEditingAnInvitation_KeepsTheOrganizerProperty()
    {
        var item = CreateItem("invite-1", organizerEmail: "boss@other.test");

        var result = CalDavIcsMutator.UpdateEvent(InvitationIcs, item, [], Me);
        var organizer = IcalCalendar.Load(result).Events.Single().Organizer;

        organizer.Value.OriginalString.Should().Be("mailto:boss@other.test");
        organizer.Parameters.Get("SCHEDULE-STATUS").Should().Be("1.2");
    }

    private static Ical.Net.DataTypes.Attendee Attendee(string email, CalendarEvent calendarEvent)
        => calendarEvent.Attendees.Single(value => string.Equals(
            CalDavUserIdentity.ExtractEmail(value.Value.OriginalString), email, StringComparison.OrdinalIgnoreCase));

    private static CalendarItem CreateItem(string remoteEventId, string organizerEmail)
        => new()
        {
            RemoteEventId = remoteEventId,
            Title = "Edited",
            StartDate = new DateTime(2026, 2, 19, 10, 0, 0),
            DurationInSeconds = 3600,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC",
            OrganizerEmail = organizerEmail,
            Status = CalendarItemStatus.Accepted,
            ShowAs = CalendarItemShowAs.Busy
        };
}
