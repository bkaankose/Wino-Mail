using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.CalDav;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Wino.Services.CalDav;
using Xunit;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Write path of the CalDAV engine with implicit scheduling (RFC 6638), against
/// <see cref="FakeCalDavServer"/> with the real client, ICS store, state store and SQLite.
/// </summary>
public sealed class CalDavSchedulingEngineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-caldav-scheduling-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCalDavServer _server = new();
    private readonly InMemoryDatabaseService _database = new();
    private CalendarService _calendarService = null!;
    private CalDavStateStore _stateStore = null!;
    private CalDavSynchronizationEngine _engine = null!;
    private MailAccount _account = null!;

    private static readonly DateTime NextWeek = DateTime.UtcNow.Date.AddDays(7).AddHours(9);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await _database.InitializeAsync();
        _calendarService = new CalendarService(_database);

        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupGet(configuration => configuration.MimeStorageFolderPath).Returns(_root);

        var processor = new ImapChangeProcessor(
            _database,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            _calendarService,
            Mock.Of<IMimeFileService>(),
            new CalendarIcsFileService(applicationConfiguration.Object));

        _stateStore = new CalDavStateStore(() => Path.Combine(_root, "CalendarIcs"));
        _engine = new CalDavSynchronizationEngine(
            new CalDavClient(_server),
            processor,
            _calendarService,
            Mock.Of<IAutoDiscoveryService>(MockBehavior.Strict),
            _stateStore);

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "CalDAV",
            Address = "user@example.test",
            SenderName = "User",
            ProviderType = MailProviderType.IMAP4,
            IsCalendarAccessGranted = true,
            CalendarIntegrationSource = AccountIntegrationSource.Dav,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                CalendarSupportMode = ImapCalendarSupportMode.CalDav,
                CalDavServiceUrl = FakeCalDavServer.ServiceUrl,
                CalDavUsername = "user@example.test",
                CalDavPassword = "test-password"
            }
        };
        await _database.Connection.InsertAsync(_account);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Accept_AsAttendee_WritesOnlyTheOwnPartStat_WithIfMatch()
    {
        _server.Put("invite", Invitation("invite", "mailto:boss@other.test", "mailto:USER@example.test"));
        await SyncAsync();
        var item = await GetItemAsync("invite");
        var previousETag = _server.ETagOf("invite");
        _server.ClearLog();

        await _engine.RespondToEventAsync(_account, item, CalendarItemStatus.Accepted);

        _server.Requests.Should().Equal("PUT /123/calendars/work/invite.ics");
        _server.ETagOf("invite").Should().NotBe(previousETag);
        var stored = ServerEvent("invite");
        PartStat(stored, "user@example.test").Should().Be("ACCEPTED");
        PartStat(stored, "colleague@other.test").Should().Be("TENTATIVE");
        stored.Status.Should().Be("CONFIRMED");
        stored.Sequence.Should().Be(2);
        stored.Organizer.Value.OriginalString.Should().Be("mailto:boss@other.test");

        var local = await _calendarService.GetCalendarItemAsync(item.Id);
        local!.Status.Should().Be(CalendarItemStatus.Accepted);
        local.IsHidden.Should().BeFalse();
        (await _calendarService.GetAttendeesAsync(item.Id))
            .Single(attendee => attendee.Email.Equals("user@example.test", StringComparison.OrdinalIgnoreCase))
            .AttendenceStatus.Should().Be(AttendeeStatus.Accepted);

        _server.ClearLog();
        await SyncAsync();
        _server.CalendarDataResponses.Should().Be(0, "the answered copy is already stored locally");
    }

    [Fact]
    public async Task Import_AsAttendee_TakesTheLocalStatusFromTheOwnPartStat()
    {
        _server.Put("pending", Invitation("pending", "mailto:boss@other.test", "mailto:user@example.test"));
        _server.Put("declined", Invitation("declined", "mailto:boss@other.test", "mailto:user@example.test")
            .Replace("ATTENDEE;RSVP=TRUE;PARTSTAT=NEEDS-ACTION:mailto:user@example.test", "ATTENDEE;PARTSTAT=DECLINED:mailto:user@example.test"));
        _server.Put("mine", Invitation("mine", "mailto:user@example.test", "mailto:colleague2@other.test"));

        await SyncAsync();

        var pending = await GetItemAsync("pending");
        pending.Status.Should().Be(CalendarItemStatus.NotResponded);
        pending.IsHidden.Should().BeFalse();

        var declined = await GetItemAsync("declined");
        declined.Status.Should().Be(CalendarItemStatus.Cancelled);
        declined.IsHidden.Should().BeTrue();

        var mine = await GetItemAsync("mine");
        mine.Status.Should().Be(CalendarItemStatus.Accepted, "the organizer's copy follows STATUS, not a PARTSTAT");
    }

    [Fact]
    public async Task Decline_AsAttendee_KeepsStatusOnTheWire_AndHidesTheEventLocally()
    {
        _server.Put("invite", Invitation("invite", "mailto:boss@other.test", "mailto:user@example.test"));
        await SyncAsync();
        var item = await GetItemAsync("invite");

        await _engine.RespondToEventAsync(_account, item, CalendarItemStatus.Cancelled);

        var stored = ServerEvent("invite");
        PartStat(stored, "user@example.test").Should().Be("DECLINED");
        stored.Status.Should().Be("CONFIRMED");
        stored.Sequence.Should().Be(2);

        var local = await _calendarService.GetCalendarItemAsync(item.Id);
        local!.Status.Should().Be(CalendarItemStatus.Cancelled);
        local.IsHidden.Should().BeTrue();
    }

    [Fact]
    public async Task Tentative_MatchesAnyAddressOfTheAddressSet()
    {
        _server.Put("invite", Invitation("invite", "mailto:boss@other.test", "mailto:Alias@Example.TEST"));
        await SyncAsync();
        await _stateStore.UpdateAsync(_account.Id, state => state.CalendarUserAddresses = ["/123/principal/", "urn:uuid:1234", "mailto:alias@example.test"]);
        var item = await GetItemAsync("invite");

        await _engine.RespondToEventAsync(_account, item, CalendarItemStatus.Tentative);

        var stored = ServerEvent("invite");
        PartStat(stored, "alias@example.test").Should().Be("TENTATIVE");
        stored.Attendees.Should().HaveCount(3, "no second line is added for the account address");
        (await _calendarService.GetAttendeesAsync(item.Id))
            .Single(attendee => attendee.Email.Equals("alias@example.test", StringComparison.OrdinalIgnoreCase))
            .AttendenceStatus.Should().Be(AttendeeStatus.Tentative);
    }

    [Fact]
    public async Task Respond_AsOrganizer_KeepsTheOrganizerBehavior()
    {
        _server.Put("mine", Invitation("mine", "mailto:user@example.test", "mailto:boss@other.test"));
        await SyncAsync();
        var item = await GetItemAsync("mine");

        await _engine.RespondToEventAsync(_account, item, CalendarItemStatus.Tentative);

        var stored = ServerEvent("mine");
        stored.Status.Should().Be("TENTATIVE");
        stored.Sequence.Should().Be(3);
        PartStat(stored, "colleague@other.test").Should().Be("TENTATIVE", "the organizer never rewrites an attendee's answer");
    }

    [Fact]
    public async Task Create_UsesTheAddressSetIdentityAsOrganizer()
    {
        await SyncAsync();
        await _stateStore.UpdateAsync(_account.Id, state => state.CalendarUserAddresses = ["urn:uuid:1234", "mailto:calendar@example.test"]);
        var calendar = await GetCalendarAsync();
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Title = "Created",
            StartDate = NextWeek,
            DurationInSeconds = 3600,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC",
            OrganizerEmail = _account.Address
        };
        var attendees = new List<CalendarEventAttendee>
        {
            new() { Id = Guid.NewGuid(), CalendarItemId = item.Id, Email = "boss@other.test", AttendenceStatus = AttendeeStatus.NeedsAction }
        };

        await _engine.CreateEventAsync(_account, item, attendees, reminders: null);

        var name = item.RemoteEventId.Split("::")[0];
        var stored = IcalCalendar.Load(_server.IcsOf(name)).Events.Single();
        stored.Organizer.Value.OriginalString.Should().Be("mailto:calendar@example.test");
        item.OrganizerEmail.Should().Be("calendar@example.test");
    }

    [Fact]
    public void ImportedResponseStatus_FollowsTheUsersPartStat()
    {
        var identity = CalDavUserIdentity.Resolve(["mailto:user@example.test"], "user@example.test");

        CalDavSynchronizationEngine.ResolveImportedResponseStatus(Remote("boss@other.test", AttendeeStatus.Declined), identity)
            .Should().Be(CalendarItemStatus.Cancelled);
        CalDavSynchronizationEngine.ResolveImportedResponseStatus(Remote("boss@other.test", AttendeeStatus.NeedsAction), identity)
            .Should().Be(CalendarItemStatus.NotResponded);
        CalDavSynchronizationEngine.ResolveImportedResponseStatus(Remote("user@example.test", AttendeeStatus.Accepted), identity)
            .Should().BeNull("the organizer's own event follows STATUS");

        static CalDavCalendarEvent Remote(string organizer, AttendeeStatus own) => new()
        {
            OrganizerEmail = organizer,
            Status = CalendarItemStatus.Accepted,
            Attendees = new List<CalDavEventAttendee> { new() { Email = "user@example.test", AttendenceStatus = own } }
        };
    }

    private static string Invitation(string uid, string organizer, string me)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Wino//FakeCalDav//EN\r\nBEGIN:VEVENT\r\n" +
           $"UID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n" +
           $"DTSTART:{NextWeek.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           $"DTEND:{NextWeek.AddHours(1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           "SUMMARY:Planning\r\nSTATUS:CONFIRMED\r\nSEQUENCE:2\r\n" +
           $"ORGANIZER;CN=Organizer:{organizer}\r\n" +
           $"ATTENDEE;CN=Organizer;ROLE=CHAIR;PARTSTAT=ACCEPTED:{organizer}\r\n" +
           $"ATTENDEE;RSVP=TRUE;PARTSTAT=NEEDS-ACTION:{me}\r\n" +
           "ATTENDEE;CN=Colleague;PARTSTAT=TENTATIVE:mailto:colleague@other.test\r\n" +
           "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private Ical.Net.CalendarComponents.CalendarEvent ServerEvent(string name)
        => IcalCalendar.Load(_server.IcsOf(name)).Events.Single();

    private static string PartStat(Ical.Net.CalendarComponents.CalendarEvent calendarEvent, string email)
        => calendarEvent.Attendees.Single(attendee => string.Equals(
            CalDavUserIdentity.ExtractEmail(attendee.Value.OriginalString), email, StringComparison.OrdinalIgnoreCase)).ParticipationStatus;

    private async Task<CalendarItem> GetItemAsync(string remoteEventId)
    {
        var item = await _calendarService.GetCalendarItemAsync((await GetCalendarAsync()).Id, remoteEventId);
        item.Should().NotBeNull();
        return item!;
    }

    private async Task SyncAsync()
    {
        var result = await _engine.SynchronizeAsync(_account, new CalendarSynchronizationOptions
        {
            AccountId = _account.Id,
            Type = CalendarSynchronizationType.CalendarEvents
        });

        result.CompletedState.Should().Be(
            SynchronizationCompletedState.Success,
            string.Join(" | ", result.Issues.Select(issue => $"{issue.ExceptionType}: {issue.Message}")));
    }

    private async Task<AccountCalendar> GetCalendarAsync()
        => (await _calendarService.GetAccountCalendarsAsync(_account.Id)).Single(calendar => calendar.RemoteCalendarId == FakeCalDavServer.CalendarId);
}
