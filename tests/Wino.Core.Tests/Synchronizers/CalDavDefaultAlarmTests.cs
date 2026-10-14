using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.CalDav;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Wino.Services.CalDav;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// CalDAV default alarms (calendar-default-alarms): VALARM parsing, seeding reminders of newly
/// imported events, and writing the default into events Wino creates.
/// </summary>
public sealed class CalDavDefaultAlarmTests : IAsyncLifetime
{
    private const string HomeDefault = "<C:default-alarm-vevent-datetime>BEGIN:VALARM&#13;\nTRIGGER:-PT15M&#13;\nACTION:DISPLAY&#13;\nEND:VALARM</C:default-alarm-vevent-datetime>";

    private CalDavEngineTestHost _host = null!;

    public async Task InitializeAsync() => _host = await CalDavEngineTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    #region Parsing

    [Fact]
    public void Parse_DisplayAlarm_BecomesPopupReminder_AndIsMarkedAsAppleDefault()
    {
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms(
            "BEGIN:VALARM\r\nX-WR-ALARMUID:AAA\r\nUID:AAA\r\nTRIGGER:-PT15M\r\nATTACH;VALUE=URI:Chord\r\nACTION:DISPLAY\r\nDESCRIPTION:Reminder\r\nEND:VALARM\r\n");

        alarms.Should().ContainSingle();
        alarms[0].Reminder!.DurationInSeconds.Should().Be(900);
        alarms[0].Reminder!.ReminderType.Should().Be(CalendarItemReminderType.Popup);
        alarms[0].Block.Should().StartWith("BEGIN:VALARM").And.EndWith("X-APPLE-DEFAULT-ALARM:TRUE\nEND:VALARM");
        alarms[0].Block.Should().Contain("TRIGGER:-PT15M").And.Contain("ATTACH;VALUE=URI:Chord");
        alarms[0].Block.Should().NotContain("UID:", "an alarm copied into another event must not reuse the stored alarm's identity");
    }

    [Fact]
    public void Parse_EmailAlarm_DayBefore_BecomesEmailReminder()
    {
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nACTION:EMAIL\nTRIGGER;RELATED=START:-P1D\nSUMMARY:x\nEND:VALARM");

        alarms.Should().ContainSingle();
        alarms[0].Reminder!.DurationInSeconds.Should().Be(86400);
        alarms[0].Reminder!.ReminderType.Should().Be(CalendarItemReminderType.Email);
    }

    [Fact]
    public void Parse_IgnoresAbsoluteTriggers_AndAppleNoneAlarm()
    {
        CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nTRIGGER;VALUE=DATE-TIME:20260101T090000Z\nACTION:DISPLAY\nEND:VALARM").Should().BeEmpty();
        CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nTRIGGER;VALUE=DATE-TIME:19760401T005545Z\nACTION:NONE\nEND:VALARM").Should().BeEmpty();
        CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nTRIGGER:-PT5M\nACTION:NONE\nEND:VALARM").Should().BeEmpty();
        CalendarIcsBuilder.ParseDefaultAlarms(string.Empty).Should().BeEmpty();
        CalendarIcsBuilder.ParseDefaultAlarms("not a valarm").Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnfoldsFoldedLines_AndReadsSeveralAlarms()
    {
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms(
            "BEGIN:VALARM\r\nTRIGGER:-PT1\r\n 0M\r\nACTION:DISPLAY\r\nEND:VALARM\r\nBEGIN:VALARM\r\nTRIGGER:-PT1H\r\nACTION:AUDIO\r\nEND:VALARM");

        alarms.Select(alarm => alarm.Reminder!.DurationInSeconds).Should().Equal(600, 3600);
    }

    [Fact]
    public void Parse_IndentedXmlValue_IsTrimmedNotUnfolded()
    {
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms("\n      BEGIN:VALARM\n      TRIGGER:-PT20M\n      ACTION:DISPLAY\n      END:VALARM\n    ");

        alarms.Should().ContainSingle().Which.Reminder!.DurationInSeconds.Should().Be(1200);
    }

    [Fact]
    public void Parse_TriggerAfterStart_IsWrittenButHasNoLocalReminder()
    {
        // Apple's all-day default "on the day at 09:00".
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nTRIGGER:PT9H\nACTION:DISPLAY\nEND:VALARM");

        alarms.Should().ContainSingle();
        alarms[0].Reminder.Should().BeNull();
        alarms[0].Block.Should().Contain("TRIGGER:PT9H");
    }

    [Theory]
    [InlineData("-PT15M", -900)]
    [InlineData("PT0S", 0)]
    [InlineData("+P1D", 86400)]
    [InlineData("-P1DT9H", -118800)]
    [InlineData("-P2W", -1209600)]
    [InlineData("-PT1H30M5S", -5405)]
    public void TryParseIcsDuration_ParsesRfc5545Durations(string value, int seconds)
    {
        CalendarIcsBuilder.TryParseIcsDuration(value, out var duration).Should().BeTrue();
        duration.TotalSeconds.Should().Be(seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("15M")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P15M")]
    [InlineData("PT15")]
    public void TryParseIcsDuration_RejectsInvalidValues(string value)
        => CalendarIcsBuilder.TryParseIcsDuration(value, out _).Should().BeFalse();

    [Fact]
    public void AddDefaultAlarms_InsertsOnlyIntoEventsWithoutAlarm()
    {
        var alarms = CalendarIcsBuilder.ParseDefaultAlarms("BEGIN:VALARM\nTRIGGER:-PT15M\nACTION:DISPLAY\nEND:VALARM");
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:b\r\nBEGIN:VALARM\r\nTRIGGER:-PT5M\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarIcsBuilder.AddDefaultAlarms(ics, alarms);

        result.Should().Contain("UID:a\r\nBEGIN:VALARM\r\nTRIGGER:-PT15M\r\nACTION:DISPLAY\r\nX-APPLE-DEFAULT-ALARM:TRUE\r\nEND:VALARM\r\nEND:VEVENT");
        CountOccurrences(result, "X-APPLE-DEFAULT-ALARM").Should().Be(1);
        result.Should().EndWith("END:VCALENDAR\r\n");
        CalendarIcsBuilder.AddDefaultAlarms(ics, []).Should().BeSameAs(ics);
    }

    [Fact]
    public void GetDefaultReminders_CalendarValueWinsOverHome_EvenWhenItMeansNone()
    {
        var state = new CalDavAccountState { DefaultAlarmVEventDateTime = "BEGIN:VALARM\nTRIGGER:-PT15M\nACTION:DISPLAY\nEND:VALARM" };
        state.Calendars["a"] = new CalDavCalendarState { RemoteCalendarId = "a", DefaultAlarmVEventDateTime = "BEGIN:VALARM\nTRIGGER:-PT1H\nACTION:DISPLAY\nEND:VALARM" };
        state.Calendars["none"] = new CalDavCalendarState { RemoteCalendarId = "none", DefaultAlarmVEventDateTime = "BEGIN:VALARM\nTRIGGER;VALUE=DATE-TIME:19760401T005545Z\nACTION:NONE\nEND:VALARM" };
        state.Calendars["inherit"] = new CalDavCalendarState { RemoteCalendarId = "inherit" };

        CalDavSynchronizationEngine.GetDefaultReminders(state, "a", isAllDay: false).Single().DurationInSeconds.Should().Be(3600);
        CalDavSynchronizationEngine.GetDefaultReminders(state, "none", isAllDay: false).Should().BeEmpty();
        CalDavSynchronizationEngine.GetDefaultReminders(state, "inherit", isAllDay: false).Single().DurationInSeconds.Should().Be(900);
        CalDavSynchronizationEngine.GetDefaultReminders(state, "inherit", isAllDay: true).Should().BeEmpty();
    }

    #endregion

    #region Import

    [Fact]
    public async Task Import_NewEventWithoutAlarm_GetsTheHomeDefault()
    {
        _host.Server.Put("plain", FakeCalDavServer.Event("plain", "Plain", CalDavEngineTestHost.NextWeek));

        await _host.SyncAsync();

        var reminders = await _host.GetRemindersAsync("plain");
        reminders.Should().ContainSingle().Which.DurationInSeconds.Should().Be(900);
    }

    [Fact]
    public async Task Import_EventWithItsOwnAlarm_KeepsOnlyThatAlarm()
    {
        _host.Server.Put("alarm", WithAlarm(FakeCalDavServer.Event("alarm", "Alarm", CalDavEngineTestHost.NextWeek), "-PT30M"));
        _host.Server.Put("absolute", WithAlarm(FakeCalDavServer.Event("absolute", "Absolute", CalDavEngineTestHost.NextWeek), null));

        await _host.SyncAsync();

        (await _host.GetRemindersAsync("alarm")).Select(reminder => reminder.DurationInSeconds).Should().Equal(1800);
        (await _host.GetRemindersAsync("absolute")).Should().BeEmpty("an event with any VALARM is never given the default");
    }

    [Fact]
    public async Task Import_ExistingItem_KeepsTheUsersReminderChoice()
    {
        _host.Server.Put("plain", FakeCalDavServer.Event("plain", "Plain", CalDavEngineTestHost.NextWeek));
        await _host.SyncAsync();

        var item = await _host.GetItemAsync("plain");
        await _host.CalendarService.SaveRemindersAsync(item.Id, []);
        _host.Server.Put("plain", FakeCalDavServer.Event("plain", "Plain renamed", CalDavEngineTestHost.NextWeek));
        await _host.SyncAsync();

        (await _host.GetItemAsync("plain")).Title.Should().Be("Plain renamed");
        (await _host.GetRemindersAsync("plain")).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_AllDayEvent_UsesTheDateDefault_FromTheCalendar()
    {
        _host.Transport.RewriteHomeListing = body => body
            .Replace("<D:displayname>Work</D:displayname>",
                "<D:displayname>Work</D:displayname><C:default-alarm-vevent-date>BEGIN:VALARM&#13;\nTRIGGER:-PT15H&#13;\nACTION:DISPLAY&#13;\nEND:VALARM</C:default-alarm-vevent-date>");
        var day = CalDavEngineTestHost.NextWeek.Date;
        _host.Server.Put("allday", AllDayEvent("allday", day));
        _host.Server.Put("timed", FakeCalDavServer.Event("timed", "Timed", CalDavEngineTestHost.NextWeek));

        await _host.SyncAsync();

        (await _host.GetRemindersAsync("allday")).Select(reminder => reminder.DurationInSeconds).Should().Equal(54000);
        (await _host.GetRemindersAsync("timed")).Should().ContainSingle().Which.DurationInSeconds.Should().Be(900, "timed events use the home datetime default");
    }

    [Fact]
    public async Task Import_WithoutServerDefault_SeedsNothing()
    {
        _host.Transport.RewriteHomeListing = body => body.Replace(HomeDefault, string.Empty);
        _host.Server.Put("plain", FakeCalDavServer.Event("plain", "Plain", CalDavEngineTestHost.NextWeek));

        await _host.SyncAsync();

        (await _host.GetRemindersAsync("plain")).Should().BeEmpty();
    }

    #endregion

    #region Create

    [Fact]
    public async Task Create_WithoutReminders_WritesTheDefaultAlarm_AndPersistsItsReminder()
    {
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("Created");

        await _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: []);

        var ics = _host.LastPutBody();
        ics.Should().Contain("BEGIN:VALARM").And.Contain("TRIGGER:-PT15M").And.Contain("X-APPLE-DEFAULT-ALARM:TRUE");
        (await _host.CalendarService.GetRemindersAsync(item.Id)).Select(reminder => reminder.DurationInSeconds).Should().Equal(900);

        var expanded = new CalDavClient(_host.Server).ExpandCalendarResource(ics, "/x.ics", "\"1\"", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        expanded.Should().ContainSingle().Which.Reminders.Should().ContainSingle().Which.DurationInSeconds.Should().Be(900);
    }

    [Fact]
    public async Task Create_WithChosenReminder_WritesNoDefaultAlarm()
    {
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("Chosen");
        var chosen = new Reminder { Id = Guid.NewGuid(), CalendarItemId = item.Id, DurationInSeconds = 300, ReminderType = CalendarItemReminderType.Popup };

        await _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: [chosen]);

        _host.LastPutBody().Should().NotContain("X-APPLE-DEFAULT-ALARM");
        (await _host.CalendarService.GetRemindersAsync(item.Id)).Select(reminder => reminder.DurationInSeconds).Should().Equal(300);
    }

    [Fact]
    public async Task Create_WithoutServerDefault_WritesNoAlarm()
    {
        _host.Transport.RewriteHomeListing = body => body.Replace(HomeDefault, string.Empty);
        await _host.SyncAsync();
        var item = await _host.NewItemAsync("No default");

        await _host.Engine.CreateEventAsync(_host.Account, item, attendees: null, reminders: []);

        _host.LastPutBody().Should().NotContain("BEGIN:VALARM");
        (await _host.CalendarService.GetRemindersAsync(item.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_NeverAddsTheDefaultAlarm()
    {
        _host.Server.Put("plain", FakeCalDavServer.Event("plain", "Plain", CalDavEngineTestHost.NextWeek));
        await _host.SyncAsync();
        var item = await _host.GetItemAsync("plain");
        item.Title = "Edited";

        await _host.Engine.UpdateEventAsync(_host.Account, item, attendees: null);

        _host.LastPutBody().Should().Contain("SUMMARY:Edited").And.NotContain("BEGIN:VALARM");
    }

    #endregion

    private static string WithAlarm(string ics, string? relativeTrigger)
        => ics.Replace("END:VEVENT", relativeTrigger == null
            ? "BEGIN:VALARM\r\nTRIGGER;VALUE=DATE-TIME:20260101T090000Z\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT"
            : $"BEGIN:VALARM\r\nTRIGGER:{relativeTrigger}\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT");

    private static string AllDayEvent(string uid, DateTime day)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Wino//FakeCalDav//EN\r\nBEGIN:VEVENT\r\n" +
           $"UID:{uid}\r\nDTSTAMP:20260101T000000Z\r\nDTSTART;VALUE=DATE:{day:yyyyMMdd}\r\nDTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}\r\nSUMMARY:All day\r\n" +
           "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}

/// <summary>
/// The real CalDAV engine, client, ICS store, state store and SQLite database over
/// <see cref="FakeCalDavServer"/>, with a transport that can rewrite the calendar home listing and
/// answer PUTs itself.
/// </summary>
internal sealed class CalDavEngineTestHost : IAsyncDisposable
{
    public static readonly DateTime NextWeek = DateTime.UtcNow.Date.AddDays(7).AddHours(9);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-caldav-t3-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryDatabaseService _database = new();

    public FakeCalDavServer Server { get; } = new();
    public InterceptingTransport Transport { get; private set; } = null!;
    public CalendarService CalendarService { get; private set; } = null!;
    public CalDavStateStore StateStore { get; private set; } = null!;
    public CalDavSynchronizationEngine Engine { get; private set; } = null!;
    public MailAccount Account { get; private set; } = null!;

    public static async Task<CalDavEngineTestHost> CreateAsync()
    {
        var host = new CalDavEngineTestHost();
        await host.InitializeAsync();
        return host;
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await _database.InitializeAsync();
        CalendarService = new CalendarService(_database);
        Transport = new InterceptingTransport(Server);

        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupGet(configuration => configuration.MimeStorageFolderPath).Returns(_root);

        var processor = new ImapChangeProcessor(
            _database,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            CalendarService,
            Mock.Of<IMimeFileService>(),
            new CalendarIcsFileService(applicationConfiguration.Object));

        StateStore = new CalDavStateStore(() => Path.Combine(_root, "CalendarIcs"));
        Engine = new CalDavSynchronizationEngine(
            new CalDavClient(Transport),
            processor,
            CalendarService,
            Mock.Of<IAutoDiscoveryService>(MockBehavior.Strict),
            StateStore);

        Account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "CalDAV",
            Address = "user@example.test",
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
        await _database.Connection.InsertAsync(Account);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public async Task SyncAsync()
    {
        var result = await Engine.SynchronizeAsync(Account, new CalendarSynchronizationOptions
        {
            AccountId = Account.Id,
            Type = CalendarSynchronizationType.CalendarEvents
        });

        result.CompletedState.Should().Be(
            SynchronizationCompletedState.Success,
            string.Join(" | ", result.Issues.Select(issue => $"{issue.ExceptionType}: {issue.Message}")));
    }

    public async Task<AccountCalendar> GetCalendarAsync()
        => (await CalendarService.GetAccountCalendarsAsync(Account.Id)).Single(calendar => calendar.RemoteCalendarId == FakeCalDavServer.CalendarId);

    public async Task<CalendarItem> GetItemAsync(string remoteEventId)
    {
        var item = await CalendarService.GetCalendarItemAsync((await GetCalendarAsync()).Id, remoteEventId);
        item.Should().NotBeNull();
        return item!;
    }

    public async Task<List<Reminder>> GetRemindersAsync(string remoteEventId)
        => await CalendarService.GetRemindersAsync((await GetItemAsync(remoteEventId)).Id);

    public async Task<CalendarItem> NewItemAsync(string title)
    {
        var calendar = await GetCalendarAsync();
        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            Title = title,
            StartDate = NextWeek,
            DurationInSeconds = 3600,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC"
        };
    }

    public string LastPutBody() => Transport.PutBodies.Should().NotBeEmpty().And.Subject.Last();

    /// <summary>Delegates to the fake server; can rewrite the home listing and answer PUTs itself.</summary>
    internal sealed class InterceptingTransport(FakeCalDavServer inner) : IDavTransport
    {
        public Func<string, string>? RewriteHomeListing { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? AnswerPut { get; set; }
        public List<string> PutBodies { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, DavAuthenticationProfile authentication, CancellationToken cancellationToken = default)
        {
            if (request.Method == HttpMethod.Put)
            {
                PutBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

                if (AnswerPut?.Invoke(request) is { } answer)
                {
                    answer.RequestMessage = request;
                    return answer;
                }
            }

            var response = await inner.SendAsync(request, authentication, cancellationToken);

            if (RewriteHomeListing != null &&
                request.Method.Method == "PROPFIND" &&
                request.RequestUri!.AbsolutePath == FakeCalDavServer.HomePath &&
                response.Content != null)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content = new StringContent(RewriteHomeListing(body), Encoding.UTF8, "application/xml");
            }

            return response;
        }
    }
}
