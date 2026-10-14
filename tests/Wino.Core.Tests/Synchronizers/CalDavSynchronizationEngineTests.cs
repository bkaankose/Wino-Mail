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

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Runs the CalDAV engine against <see cref="FakeCalDavServer"/> with the real client, ICS store,
/// state store, change processor and an SQLite database.
/// </summary>
public sealed class CalDavSynchronizationEngineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-caldav-engine-tests", Guid.NewGuid().ToString("N"));
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
    public async Task InitialSync_DownloadsEveryEvent_AndUnchangedSyncSendsOnlyTheHomeListing()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek.AddDays(1)));
        _server.Put("weekly", FakeCalDavServer.Event("weekly", "Weekly", NextWeek, "FREQ=WEEKLY;COUNT=4"));

        await SyncAsync();

        var calendar = await GetCalendarAsync();
        (await GetTitlesAsync(calendar)).Should().Contain(["One", "Two", "Weekly"]);
        (await _database.Connection.Table<CalendarItem>().CountAsync()).Should().Be(2 + 1 + 4, "the series is a master and four occurrences");
        _server.Requests.Should().Contain("REPORT /123/calendars/work/ calendar-query");
        _server.Requests.Should().Contain("REPORT /123/calendars/work/ multiget");
        _server.Requests.Should().NotContain(request => request.EndsWith("calendar-query+data"));
        _server.PreferHeaders.Should().Contain("REPORT return=minimal").And.Contain("PROPFIND return=minimal");

        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().Equal("PROPFIND /123/calendars/");
        _server.CalendarDataResponses.Should().Be(0);
    }

    [Fact]
    public async Task Delta_UsesSyncCollection_DownloadsOnlyChangedResources_AndRemovesDeletedOnes()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        _server.Put("weekly", FakeCalDavServer.Event("weekly", "Weekly", NextWeek, "FREQ=WEEKLY;COUNT=4"));
        await SyncAsync();

        _server.Put("one", FakeCalDavServer.Event("one", "One renamed", NextWeek));
        _server.Delete("weekly");
        _server.Put("three", FakeCalDavServer.Event("three", "Three", NextWeek));
        _server.ClearLog();

        await SyncAsync();

        _server.Requests.Should().Contain("REPORT /123/calendars/work/ sync-collection");
        _server.Requests.Should().NotContain(request => request.Contains("calendar-query"));
        _server.CalendarDataResponses.Should().Be(2, "only the changed and the new resource are downloaded");

        var calendar = await GetCalendarAsync();
        (await GetTitlesAsync(calendar)).Should().BeEquivalentTo(["One renamed", "Two", "Three"]);
        (await _database.Connection.Table<CalendarItem>().CountAsync()).Should().Be(3);

        var state = await _stateStore.GetAsync(_account.Id);
        state.FindCalendar(FakeCalDavServer.CalendarId)!.LastSyncTokenFromReport.Should().BeTrue();
    }

    [Fact]
    public async Task RefusedSyncToken_RecoversWithEtagListing()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        await SyncAsync();
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        await SyncAsync();

        _server.OldestValidRevision = int.MaxValue;
        _server.Put("one", FakeCalDavServer.Event("one", "One changed", NextWeek));
        _server.ClearLog();

        await SyncAsync();

        _server.Requests.Should().ContainInOrder(
            "REPORT /123/calendars/work/ sync-collection",
            "REPORT /123/calendars/work/ calendar-query",
            "REPORT /123/calendars/work/ multiget");
        _server.CalendarDataResponses.Should().Be(1);
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One changed", "Two"]);

        var state = (await _stateStore.GetAsync(_account.Id)).FindCalendar(FakeCalDavServer.CalendarId)!;
        state.SyncCollectionRefused.Should().BeFalse("an expired REPORT token is not a reason to stop using sync-collection");
        state.LastSyncTokenFromReport.Should().BeFalse();
    }

    [Fact]
    public async Task EmptyMultistatusForSyncCollection_FallsBackToEtagListing_AndIsRemembered()
    {
        // Yahoo and Zimbra answer a REPORT they do not implement with an empty 207.
        _server.SupportsSyncCollection = false;
        _server.EmptyMultistatusForUnsupportedReports = true;
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        await SyncAsync();

        _server.Put("one", FakeCalDavServer.Event("one", "One changed", NextWeek));
        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().ContainInOrder(
            "REPORT /123/calendars/work/ sync-collection",
            "REPORT /123/calendars/work/ calendar-query",
            "REPORT /123/calendars/work/ multiget");
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One changed"]);
        (await _stateStore.GetAsync(_account.Id)).FindCalendar(FakeCalDavServer.CalendarId)!.SyncCollectionRefused.Should().BeTrue();

        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().NotContain(request => request.EndsWith("sync-collection"));
        _server.Requests.Should().Contain("REPORT /123/calendars/work/ calendar-query");
        _server.CalendarDataResponses.Should().Be(1);
    }

    [Fact]
    public async Task ServerWithoutSyncCollection_UsesEtagListing_AndDetectsDeletion()
    {
        _server.SupportsSyncCollection = false;
        _server.AdvertiseSyncCollection = false;
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        await SyncAsync();

        _server.Delete("two");
        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().Equal("PROPFIND /123/calendars/", "REPORT /123/calendars/work/ calendar-query");
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One"]);
    }

    [Fact]
    public async Task TruncatedSyncCollection_FollowsEveryPage()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        await SyncAsync();
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        await SyncAsync();

        _server.SyncPageSize = 2;
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
            _server.Put(name, FakeCalDavServer.Event(name, name.ToUpperInvariant(), NextWeek));
        _server.Delete("one");
        _server.ClearLog();

        await SyncAsync();

        _server.Requests.Count(request => request.EndsWith("sync-collection")).Should().Be(3);
        _server.CalendarDataResponses.Should().Be(5);
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["Two", "A", "B", "C", "D", "E"]);
    }

    [Fact]
    public async Task RefusedEtagListing_FallsBackToTheWholeWindowQuery()
    {
        _server.SupportsSyncCollection = false;
        _server.AdvertiseSyncCollection = false;
        _server.RefuseEtagQuery = true;
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));
        await SyncAsync();

        _server.Requests.Should().Contain(request => request.EndsWith("calendar-query+data"));
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One", "Two"]);

        _server.Delete("two");
        await SyncAsync();

        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One"]);
    }

    [Fact]
    public async Task RefusedMultiget_FallsBackToGet_AndIsRemembered()
    {
        _server.SupportsMultiget = false;
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        _server.Put("two", FakeCalDavServer.Event("two", "Two", NextWeek));

        await SyncAsync();

        _server.Requests.Should().Contain("REPORT /123/calendars/work/ multiget");
        _server.Requests.Count(request => request.StartsWith("GET ")).Should().Be(2);
        (await GetTitlesAsync(await GetCalendarAsync())).Should().BeEquivalentTo(["One", "Two"]);
        (await _stateStore.GetAsync(_account.Id)).FindCalendar(FakeCalDavServer.CalendarId)!.MultigetRefused.Should().BeTrue();

        _server.Put("three", FakeCalDavServer.Event("three", "Three", NextWeek));
        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().NotContain(request => request.EndsWith("multiget"));
        _server.Requests.Should().Contain("GET /123/calendars/work/three.ics");
    }

    [Fact]
    public async Task Reanchor_WithUnchangedServer_ExpandsStoredIcsWithoutDownloading()
    {
        _server.Put("weekly", FakeCalDavServer.Event("weekly", "Weekly", NextWeek, "FREQ=WEEKLY;COUNT=4"));
        await SyncAsync();

        var calendar = await GetCalendarAsync();
        var (providerToken, _) = CalendarSyncWindowToken.Decode(calendar.SynchronizationDeltaToken);
        calendar.SynchronizationDeltaToken = CalendarSyncWindowToken.Encode(providerToken, DateTimeOffset.UtcNow.AddDays(100));
        await _calendarService.UpdateAccountCalendarAsync(calendar);

        // An occurrence that is missing locally must come back from the stored copy.
        var occurrence = (await _database.Connection.Table<CalendarItem>().ToListAsync()).First(item => item.IsRecurringChild);
        await _database.Connection.DeleteAsync<CalendarItem>(occurrence.Id);
        _server.ClearLog();

        await SyncAsync();

        _server.Requests.Should().Equal("PROPFIND /123/calendars/");
        _server.CalendarDataResponses.Should().Be(0);
        (await _database.Connection.Table<CalendarItem>().CountAsync()).Should().Be(5);
        var (_, windowEnd) = CalendarSyncWindowToken.Decode((await GetCalendarAsync()).SynchronizationDeltaToken);
        windowEnd!.Value.Date.Should().Be(DateTimeOffset.UtcNow.AddYears(2).Date);
    }

    [Fact]
    public async Task WritePath_UpdatesWithIfMatch_AndTheNextSyncDownloadsNothing()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        await SyncAsync();

        var calendar = await GetCalendarAsync();
        var item = await _calendarService.GetCalendarItemAsync(calendar.Id, "one");
        item.Should().NotBeNull();
        var previousETag = _server.ETagOf("one");
        item!.Title = "One edited";
        _server.ClearLog();

        await _engine.UpdateEventAsync(_account, item, attendees: null);

        _server.Requests.Should().Equal("PUT /123/calendars/work/one.ics");
        _server.IcsOf("one").Should().Contain("SUMMARY:One edited");
        _server.ETagOf("one").Should().NotBe(previousETag);

        _server.ClearLog();
        await SyncAsync();

        _server.Requests.Should().Contain("REPORT /123/calendars/work/ sync-collection");
        _server.CalendarDataResponses.Should().Be(0, "the written resource is already current locally");
    }

    [Fact]
    public async Task WritePath_StaleETag_IsRejectedByTheServer()
    {
        _server.Put("one", FakeCalDavServer.Event("one", "One", NextWeek));
        await SyncAsync();
        _server.Put("one", FakeCalDavServer.Event("one", "Changed elsewhere", NextWeek));

        var item = await _calendarService.GetCalendarItemAsync((await GetCalendarAsync()).Id, "one");
        item!.Title = "Local edit";

        var write = () => _engine.UpdateEventAsync(_account, item, attendees: null);

        (await write.Should().ThrowAsync<Wino.Core.Domain.Models.CardDav.DavRequestException>()).Which.StatusCode.Should().Be(412);
        _server.IcsOf("one").Should().Contain("Changed elsewhere");
    }

    [Fact]
    public async Task Discovery_RecordsCapabilities_AndSkipsTaskLists()
    {
        await SyncAsync();

        (await _calendarService.GetAccountCalendarsAsync(_account.Id)).Select(calendar => calendar.Name).Should().Equal("Work");

        var state = await _stateStore.GetAsync(_account.Id);
        state.PrincipalHref.Should().Be(FakeCalDavServer.Origin + FakeCalDavServer.PrincipalPath);
        state.CalendarHomeHrefs.Should().Equal(FakeCalDavServer.Origin + FakeCalDavServer.HomePath);
        state.CalendarUserAddresses.Should().Equal("mailto:user@example.test", "urn:uuid:1234");
        state.ScheduleOutboxHref.Should().Be(FakeCalDavServer.Origin + "/123/calendars/outbox/");
        state.DavCapabilities.Should().Contain(["calendar-auto-schedule", "calendar-managed-attachments"]);
        state.DefaultAlarmVEventDateTime.Should().Contain("TRIGGER:-PT15M");

        var work = state.FindCalendar(FakeCalDavServer.CalendarId)!;
        work.SupportedComponents.Should().Equal("VEVENT");
        work.SupportsSyncCollection.Should().BeTrue();
        work.SupportsMultiget.Should().BeTrue();
        work.QuotaAvailableBytes.Should().Be(1048576);
        work.QuotaUsedBytes.Should().Be(2048);
        work.MaxAttendeesPerInstance.Should().Be(300);
        work.MaxResourceSize.Should().Be(0);
        work.OwnerHref.Should().Be("/123/principal/");
        work.LastSuccessfulSyncUtc.Should().NotBeNull();

        var tasks = state.FindCalendar(FakeCalDavServer.Origin + FakeCalDavServer.TasksPath.TrimEnd('/'))!;
        tasks.SupportedComponents.Should().Equal("VTODO");
        tasks.SupportsEvents.Should().BeFalse();

        var capabilities = CalendarCapabilityService.FromCalDavState(state, work);
        capabilities.Source.Should().Be(CalendarCapabilitySource.CalDav);
        capabilities.IsServerReported.Should().BeTrue();
        capabilities.SupportsAttachments.Should().BeTrue();
        capabilities.SendsInvitations.Should().BeTrue();
        capabilities.SupportsDefaultAlarms.Should().BeTrue();
        capabilities.MaxAttendees.Should().Be(300);
        capabilities.QuotaAvailableBytes.Should().Be(1048576);
        capabilities.MaxResourceSize.Should().BeNull("0 means no limit");
    }

    [Fact]
    public async Task ServerWithoutDavHeader_ReportsConservativeCapabilities()
    {
        _server.DavHeader = string.Empty;

        await SyncAsync();

        var capabilities = CalendarCapabilityService.FromCalDavState(await _stateStore.GetAsync(_account.Id), calendar: null);
        capabilities.SupportsAttachments.Should().BeFalse();
        capabilities.SendsInvitations.Should().BeFalse();
        capabilities.SupportsAttendees.Should().BeTrue();
        capabilities.SupportsDefaultAlarms.Should().BeTrue("the home still reports a default alarm");
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
            string.Join(" | ", result.Issues.Select(issue => $"{issue.ExceptionType}: {issue.Message}")) + " after " + string.Join(", ", _server.Requests));
    }

    private async Task<AccountCalendar> GetCalendarAsync()
        => (await _calendarService.GetAccountCalendarsAsync(_account.Id)).Single(calendar => calendar.RemoteCalendarId == FakeCalDavServer.CalendarId);

    private async Task<List<string>> GetTitlesAsync(AccountCalendar calendar)
        => (await _database.Connection.Table<CalendarItem>().Where(item => item.CalendarId == calendar.Id).ToListAsync())
            .Where(item => !item.IsRecurringChild)
            .Select(item => item.Title)
            .ToList();
}
