using System.Globalization;
using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.Mail;
using Wino.Core.Synchronizers.CalDav;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Wino.Services.CalDav;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// CalDAV event attachments: ATTACH parsing, import into <see cref="CalendarAttachment"/> rows,
/// downloads (credentials only for the server's own origins) and RFC 8607 managed attachments
/// against <see cref="FakeCalDavServer"/>.
/// </summary>
public sealed class CalDavAttachmentTests : IAsyncLifetime
{
    private const string ForeignOrigin = "https://files.other.test";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-caldav-attachment-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCalDavServer _server = new();
    private readonly InMemoryDatabaseService _database = new();
    private CalendarService _calendarService = null!;
    private CalDavStateStore _stateStore = null!;
    private CalDavSynchronizationEngine _engine = null!;
    private ImapChangeProcessor _processor = null!;
    private TestConfiguration _configuration = null!;
    private MailAccount _account = null!;

    private static readonly DateTime NextWeek = DateTime.UtcNow.Date.AddDays(7).AddHours(9);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await _database.InitializeAsync();
        _calendarService = new CalendarService(_database);
        _configuration = new TestConfiguration { ApplicationDataFolderPath = _root };

        _processor = new ImapChangeProcessor(
            _database,
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            _calendarService,
            Mock.Of<IMimeFileService>(),
            new CalendarIcsFileService(_configuration),
            applicationConfiguration: _configuration);

        _stateStore = new CalDavStateStore(() => Path.Combine(_root, "CalendarIcs"));
        _engine = new CalDavSynchronizationEngine(
            new CalDavClient(_server),
            _processor,
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

    #region Parsing

    [Fact]
    public void Parse_UriWithParameters_ReadsEveryParameter()
    {
        var events = Expand(EventWith("ATTACH;FMTTYPE=application/pdf;SIZE=2048;FILENAME=Agenda.pdf;MANAGED-ID=abc-1:https://files.example.test/a/b/opaque"));

        var attachment = events.Single().Attachments.Should().ContainSingle().Subject;
        attachment.Uri.Should().Be("https://files.example.test/a/b/opaque");
        attachment.FileName.Should().Be("Agenda.pdf");
        attachment.ContentType.Should().Be("application/pdf");
        attachment.Size.Should().Be(2048);
        attachment.ManagedId.Should().Be("abc-1");
        attachment.RemoteAttachmentId.Should().Be("abc-1", "a managed attachment is identified by its MANAGED-ID");
        attachment.IsInline.Should().BeFalse();
    }

    [Fact]
    public void Parse_InlineBinary_DecodesTheData()
    {
        var content = Encoding.UTF8.GetBytes("inline attachment body");
        var events = Expand(EventWith($"ATTACH;FMTTYPE=text/plain;ENCODING=BASE64;VALUE=BINARY;FILENAME=inline.txt:{Convert.ToBase64String(content)}"));

        var attachment = events.Single().Attachments.Should().ContainSingle().Subject;
        attachment.InlineData.Should().Equal(content);
        attachment.Size.Should().Be(content.Length);
        attachment.FileName.Should().Be("inline.txt");
        attachment.Uri.Should().BeEmpty();
        attachment.RemoteAttachmentId.Should().StartWith(CalDavEventAttachment.InlineIdPrefix);
    }

    [Fact]
    public void Parse_UriWithoutParameters_UsesTheLastSegmentAndUnknownSize()
    {
        var events = Expand(EventWith("ATTACH:https://files.example.test/docs/Quarterly%20Report.docx"));

        var attachment = events.Single().Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("Quarterly Report.docx");
        attachment.ContentType.Should().BeEmpty();
        attachment.Size.Should().Be(0);
        attachment.ManagedId.Should().BeEmpty();
        attachment.RemoteAttachmentId.Should().Be("https://files.example.test/docs/Quarterly%20Report.docx");
    }

    [Fact]
    public void ReadEventAttachments_PrefersTheOverriddenOccurrence()
    {
        var start = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Wino//Test//EN\r\n" +
                  "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20261102T090000Z\r\nDTEND:20261102T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:S\r\n" +
                  "ATTACH;FILENAME=master.txt:https://files.example.test/master.txt\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260101T000000Z\r\nRECURRENCE-ID:20261103T090000Z\r\nDTSTART:20261103T110000Z\r\nDTEND:20261103T120000Z\r\nSUMMARY:S moved\r\n" +
                  "ATTACH;FILENAME=override.txt:https://files.example.test/override.txt\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var client = new CalDavClient(_server);
        var occurrences = client.ExpandCalendarResource(ics, "h", "e", start.AddDays(-1), start.AddDays(5));
        var movedKey = occurrences.Single(value => value.Title == "S moved").RemoteEventId;

        client.ReadEventAttachments(ics, "series").Single().FileName.Should().Be("master.txt");
        client.ReadEventAttachments(ics, movedKey).Single().FileName.Should().Be("override.txt");
        client.ReadEventAttachments("not ics", "series").Should().BeEmpty();
    }

    [Fact]
    public void RemoveManagedAttachments_DropsOnlyThatPropertyAndKeepsFolding()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nDESCRIPTION:a long\r\n  folded line\r\n" +
                  "ATTACH;FILENAME=a.txt;MANAGED-ID=keep:https://f.test/a\r\n" +
                  "ATTACH;FILENAME=b.txt;MANAGED\r\n -ID=drop:https://f.test/b\r\n" +
                  "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalDavAttachmentIcs.RemoveManagedAttachments(ics, ["drop"]);

        result.Should().Be("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nDESCRIPTION:a long\r\n  folded line\r\n" +
                           "ATTACH;FILENAME=a.txt;MANAGED-ID=keep:https://f.test/a\r\n" +
                           "END:VEVENT\r\nEND:VCALENDAR\r\n");
    }

    [Fact]
    public void ContentDisposition_QuotesAsciiNames_AndAddsFileNameStarForOthers()
    {
        CalDavClient.BuildContentDisposition("note.txt").Should().Be("attachment;filename=\"note.txt\"");
        CalDavClient.BuildContentDisposition("Ağ \"x\".txt").Should().Be("attachment;filename=\"A_ _x_.txt\";filename*=UTF-8''A%C4%9F%20%22x%22.txt");
    }

    #endregion

    #region Import

    [Fact]
    public async Task Import_CreatesRows_AndReplaceKeepsDownloadedCopies()
    {
        _server.Put("one", EventWith(
            "ATTACH;FMTTYPE=application/pdf;SIZE=10;FILENAME=keep.pdf:https://caldav.example.test/files/keep.pdf",
            "ATTACH:https://caldav.example.test/files/drop.txt"));
        await SyncAsync();

        var item = await GetItemAsync("one");
        var rows = await _calendarService.GetAttachmentsAsync(item.Id);
        rows.Select(row => row.FileName).Should().BeEquivalentTo(["keep.pdf", "drop.txt"]);
        rows.Single(row => row.FileName == "drop.txt").ContentType.Should().Be("text/plain", "the type comes from the extension without FMTTYPE");
        rows.Should().OnlyContain(row => !row.IsDownloaded);

        var keep = rows.Single(row => row.FileName == "keep.pdf");
        keep.RemoteAttachmentId.Should().Be("https://caldav.example.test/files/keep.pdf");
        keep.Size.Should().Be(10);
        var localCopy = Path.Combine(_root, "keep-copy.pdf");
        await File.WriteAllTextAsync(localCopy, "pdf");
        await _calendarService.MarkAttachmentDownloadedAsync(keep.Id, localCopy);

        _server.Put("one", EventWith(
            "ATTACH;FMTTYPE=application/pdf;SIZE=10;FILENAME=keep.pdf:https://caldav.example.test/files/keep.pdf",
            "ATTACH;FILENAME=new.png:https://caldav.example.test/files/new.png"));
        await SyncAsync();

        rows = await _calendarService.GetAttachmentsAsync(item.Id);
        rows.Select(row => row.FileName).Should().BeEquivalentTo(["keep.pdf", "new.png"]);
        var kept = rows.Single(row => row.FileName == "keep.pdf");
        kept.Id.Should().Be(keep.Id);
        kept.IsDownloaded.Should().BeTrue();
        kept.LocalFilePath.Should().Be(localCopy);

        _server.Put("one", EventWith());
        await SyncAsync();

        (await _calendarService.GetAttachmentsAsync(item.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_InlineBinary_IsStoredAndMarkedDownloaded()
    {
        var content = Encoding.UTF8.GetBytes("inline body");
        _server.Put("one", EventWith($"ATTACH;FMTTYPE=text/plain;ENCODING=BASE64;VALUE=BINARY;FILENAME=in.txt:{Convert.ToBase64String(content)}"));

        await SyncAsync();

        var item = await GetItemAsync("one");
        var row = (await _calendarService.GetAttachmentsAsync(item.Id)).Should().ContainSingle().Subject;
        row.IsDownloaded.Should().BeTrue();
        row.LocalFilePath.Should().Be(Path.Combine(((IApplicationConfiguration)_configuration).CalendarAttachmentsFolderPath, item.Id.ToString("N"), "in.txt"));
        (await File.ReadAllBytesAsync(row.LocalFilePath)).Should().Equal(content);
    }

    [Fact]
    public async Task Update_KeepsAttachProperties_ThroughTheIcsMutator()
    {
        _server.Put("one", EventWith("ATTACH;FMTTYPE=text/plain;SIZE=5;FILENAME=a.txt;MANAGED-ID=mid-77:https://caldav.example.test/attachments/mid-77/a.txt"));
        await SyncAsync();

        var item = await GetItemAsync("one");
        item.Title = "Renamed";
        await _engine.UpdateEventAsync(_account, item, attendees: null);

        var written = new CalDavClient(_server).ExpandCalendarResource(_server.IcsOf("one"), "h", "e", NextWeek.AddDays(-1), NextWeek.AddDays(1)).Single();
        written.Title.Should().Be("Renamed");
        written.Attachments.Should().ContainSingle().Which.ManagedId.Should().Be("mid-77");
    }

    #endregion

    #region Download

    [Fact]
    public async Task Download_SameOrigin_SendsAccountCredentials()
    {
        var content = Encoding.UTF8.GetBytes("same origin file");
        _server.PutFile(FakeCalDavServer.Origin + "/files/report.txt", content);
        _server.Put("one", EventWith("ATTACH;FILENAME=report.txt:" + FakeCalDavServer.Origin + "/files/report.txt"));
        await SyncAsync();

        var (item, row) = await GetSingleAttachmentAsync("one");
        var target = Path.Combine(_root, "downloads", "report.txt");

        await _engine.DownloadAttachmentAsync(_account, item, row, target);

        (await File.ReadAllBytesAsync(target)).Should().Equal(content);
        File.Exists(target + ".part").Should().BeFalse();
        _server.Authentications.Should().Contain("GET caldav.example.test/files/report.txt Basic");
        var stored = (await _calendarService.GetAttachmentsAsync(item.Id)).Single();
        stored.IsDownloaded.Should().BeTrue();
        stored.LocalFilePath.Should().Be(target);
    }

    [Fact]
    public async Task Download_ForeignOrigin_SendsNoCredentials()
    {
        var content = Encoding.UTF8.GetBytes("foreign file");
        _server.PutFile(ForeignOrigin + "/x/opaque-token", content);
        _server.Put("one", EventWith("ATTACH;FILENAME=foreign.txt;MANAGED-ID=m1:" + ForeignOrigin + "/x/opaque-token"));
        await SyncAsync();

        var (item, row) = await GetSingleAttachmentAsync("one");
        row.RemoteAttachmentId.Should().Be("m1");
        var target = Path.Combine(_root, "downloads", "foreign.txt");

        await _engine.DownloadAttachmentAsync(_account, item, row, target);

        (await File.ReadAllBytesAsync(target)).Should().Equal(content);
        _server.Authentications.Should().Contain("GET files.other.test/x/opaque-token Anonymous");
        _server.Authentications.Should().NotContain(entry => entry.StartsWith("GET files.other.test", StringComparison.Ordinal) && entry.EndsWith("Basic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Download_ForeignPlainHttp_IsRefused()
    {
        _server.Put("one", EventWith("ATTACH;FILENAME=x.txt:http://files.other.test/x.txt"));
        await SyncAsync();
        var (item, row) = await GetSingleAttachmentAsync("one");

        var download = () => _engine.DownloadAttachmentAsync(_account, item, row, Path.Combine(_root, "x.txt"));

        await download.Should().ThrowAsync<NotSupportedException>();
        File.Exists(Path.Combine(_root, "x.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Download_AboveTheLimit_Throws()
    {
        _server.PutFile(ForeignOrigin + "/big", new byte[64]);
        var settings = new CalDavConnectionSettings { ServiceUri = new Uri(FakeCalDavServer.ServiceUrl), Username = "u", Password = "p" };
        using var destination = new MemoryStream();

        var download = () => new CalDavClient(_server).DownloadAttachmentAsync(settings, new Uri(ForeignOrigin + "/big"), false, destination, maximumBytes: 16);

        await download.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task LocalAccount_DownloadCopiesTheStoredFile()
    {
        var stored = Path.Combine(_root, "stored.txt");
        await File.WriteAllTextAsync(stored, "local");
        var attachment = new CalendarAttachment { Id = Guid.NewGuid(), CalendarItemId = Guid.NewGuid(), FileName = "stored.txt", IsDownloaded = true, LocalFilePath = stored };
        await _calendarService.InsertOrReplaceAttachmentsAsync([attachment]);
        var target = Path.Combine(_root, "open", "stored.txt");

        await ImapSynchronizer.LocalCalendarOperationHandler.CopyStoredAttachmentAsync(attachment, target, _calendarService);

        (await File.ReadAllTextAsync(target)).Should().Be("local");
    }

    #endregion

    #region Managed attachments

    [Fact]
    public async Task Create_UploadsWithAttachmentAdd_AndStoresTheServerAnswer()
    {
        await SyncAsync();
        var file = await WriteFileAsync("note.txt", "hello attachment");
        var item = await NewItemAsync("new-one");

        await _engine.CreateEventAsync(_account, item, [], [], [Draft(file)]);

        var action = _server.AttachmentActions.Should().ContainSingle().Subject;
        action.Path.Should().Be(FakeCalDavServer.CalendarPath + "new-one.ics");
        action.Query.Should().Be("action=attachment-add");
        action.ContentType.Should().Be("text/plain");
        var disposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(action.ContentDisposition);
        disposition.DispositionType.Should().Be("attachment");
        disposition.FileName.Should().Be("note.txt");
        action.IfMatch.Should().NotBeEmpty("the ETag of the PUT is sent");
        Encoding.UTF8.GetString(action.Body).Should().Be("hello attachment");
        _server.Requests.Should().NotContain(request => request.StartsWith("GET", StringComparison.Ordinal), "the answer carried the ETag and the calendar data");

        var snapshot = await _processor.GetCalendarItemIcsAsync(_account.Id, item.CalendarId, item.Id);
        snapshot!.ETag.Should().Be(_server.ETagOf("new-one"));
        snapshot.IcsContent.Should().Contain("MANAGED-ID=mid-1");

        var row = (await _calendarService.GetAttachmentsAsync(item.Id)).Should().ContainSingle().Subject;
        row.RemoteAttachmentId.Should().Be("mid-1");
        row.FileName.Should().Be("note.txt");
        row.Size.Should().Be(16);

        _server.ClearLog();
        await SyncAsync();
        _server.CalendarDataResponses.Should().Be(0, "the stored ETag is current");
    }

    [Fact]
    public async Task Create_WithBareAnswer_ReadsTheResourceBack_AndFindsTheManagedId()
    {
        _server.BareAttachmentResponses = true;
        _server.AttachmentOrigin = ForeignOrigin;
        await SyncAsync();
        var file = await WriteFileAsync("bare.txt", "bare");
        var item = await NewItemAsync("bare-one");

        await _engine.CreateEventAsync(_account, item, [], [], [Draft(file)]);

        _server.Requests.Should().Contain("GET " + FakeCalDavServer.CalendarPath + "bare-one.ics");
        var snapshot = await _processor.GetCalendarItemIcsAsync(_account.Id, item.CalendarId, item.Id);
        snapshot!.ETag.Should().Be(_server.ETagOf("bare-one"));

        var row = (await _calendarService.GetAttachmentsAsync(item.Id)).Should().ContainSingle().Subject;
        row.RemoteAttachmentId.Should().Be("mid-1");

        var target = Path.Combine(_root, "downloads", "bare.txt");
        await _engine.DownloadAttachmentAsync(_account, item, row, target);
        (await File.ReadAllTextAsync(target)).Should().Be("bare");
        _server.Authentications.Should().Contain("GET files.other.test/attachments/mid-1/bare.txt Anonymous");
    }

    [Fact]
    public async Task Update_RemovesDroppedManagedAttachment_WithAttachmentRemove()
    {
        var (item, row) = await CreateWithAttachmentAsync();
        _server.AttachmentActions.Clear();

        await _engine.UpdateEventAsync(_account, item, attendees: null, attachments: []);

        var action = _server.AttachmentActions.Should().ContainSingle().Subject;
        action.Query.Should().Be("action=attachment-remove&managed-id=" + row.RemoteAttachmentId);
        action.IfMatch.Should().NotBeEmpty();
        _server.IcsOf("new-one").Should().NotContain("ATTACH");
        (await _calendarService.GetAttachmentsAsync(item.Id)).Should().BeEmpty();
        (await _processor.GetCalendarItemIcsAsync(_account.Id, item.CalendarId, item.Id))!.ETag.Should().Be(_server.ETagOf("new-one"));
    }

    [Fact]
    public async Task Update_WhenTheServerKeepsTheAttachment_RemovesItWithPut()
    {
        var (item, row) = await CreateWithAttachmentAsync();
        _server.IgnoreAttachmentRemove = true;
        _server.ClearLog();

        await _engine.UpdateEventAsync(_account, item, attendees: null, attachments: []);

        _server.Requests.Should().Contain("PUT " + FakeCalDavServer.CalendarPath + "new-one.ics");
        _server.IcsOf("new-one").Should().NotContain("MANAGED-ID=" + row.RemoteAttachmentId);
        _server.IcsOf("new-one").Should().Contain("SUMMARY:New one");
        (await _calendarService.GetAttachmentsAsync(item.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_KeepingEveryAttachment_SendsNoAttachmentAction()
    {
        var (item, row) = await CreateWithAttachmentAsync();
        _server.AttachmentActions.Clear();

        await _engine.UpdateEventAsync(_account, item, attendees: null, attachments: [new CalendarEventComposeAttachmentDraft { Id = row.Id, FileName = row.FileName }]);

        _server.AttachmentActions.Should().BeEmpty();
        (await _calendarService.GetAttachmentsAsync(item.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task ServerWithoutManagedAttachments_DoesNotUpload()
    {
        _server.DavHeader = "1, 3, calendar-access";
        await SyncAsync();
        var file = await WriteFileAsync("ignored.txt", "x");
        var item = await NewItemAsync("plain-one");

        await _engine.CreateEventAsync(_account, item, [], [], [Draft(file)]);

        _server.Has("plain-one").Should().BeTrue("the event itself is still created");
        _server.AttachmentActions.Should().BeEmpty();
        _server.IcsOf("plain-one").Should().NotContain("ATTACH");
        (await _calendarService.GetAttachmentsAsync(item.Id)).Should().BeEmpty("no silent local copy is made for CalDAV");
        CalendarCapabilityService.FromCalDavState(await _stateStore.GetAsync(_account.Id), calendar: null).SupportsAttachments.Should().BeFalse();
    }

    #endregion

    #region Helpers

    private async Task<(CalendarItem Item, CalendarAttachment Row)> CreateWithAttachmentAsync()
    {
        await SyncAsync();
        var file = await WriteFileAsync("note.txt", "hello attachment");
        var item = await NewItemAsync("new-one");
        await _engine.CreateEventAsync(_account, item, [], [], [Draft(file)]);

        var row = (await _calendarService.GetAttachmentsAsync(item.Id)).Single();
        return (item, row);
    }

    private async Task<CalendarItem> NewItemAsync(string uid)
    {
        var calendar = await GetCalendarAsync();
        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            AssignedCalendar = calendar,
            RemoteEventId = uid,
            Title = "New one",
            StartDate = NextWeek,
            DurationInSeconds = 3600,
            StartTimeZone = TimeZoneInfo.Utc.Id,
            EndTimeZone = TimeZoneInfo.Utc.Id
        };
    }

    private static CalendarEventComposeAttachmentDraft Draft(string path) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        FileName = Path.GetFileName(path),
        Size = new FileInfo(path).Length
    };

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var folder = Path.Combine(_root, "picked");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private async Task<(CalendarItem Item, CalendarAttachment Row)> GetSingleAttachmentAsync(string uid)
    {
        var item = await GetItemAsync(uid);
        var row = (await _calendarService.GetAttachmentsAsync(item.Id)).Single();
        return (item, row);
    }

    private async Task<CalendarItem> GetItemAsync(string uid)
    {
        var item = await _calendarService.GetCalendarItemAsync((await GetCalendarAsync()).Id, uid);
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

    private IReadOnlyList<CalDavCalendarEvent> Expand(string ics)
        => new CalDavClient(_server).ExpandCalendarResource(ics, _server.HrefOf("x"), "\"e\"", NextWeek.AddDays(-1), NextWeek.AddDays(1));

    private static string EventWith(params string[] attachLines)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Wino//FakeCalDav//EN\r\nBEGIN:VEVENT\r\n" +
           "UID:one\r\nDTSTAMP:20260101T000000Z\r\n" +
           $"DTSTART:{NextWeek.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           $"DTEND:{NextWeek.AddHours(1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           "SUMMARY:One\r\n" +
           string.Concat(attachLines.Select(line => line + "\r\n")) +
           "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private sealed class TestConfiguration : IApplicationConfiguration
    {
        public string ApplicationDataFolderPath { get; set; } = string.Empty;
        public string PublisherSharedFolderPath { get; set; } = string.Empty;
        public string ApplicationTempFolderPath { get; set; } = string.Empty;
        public string SentryDNS => string.Empty;
    }

    #endregion
}
