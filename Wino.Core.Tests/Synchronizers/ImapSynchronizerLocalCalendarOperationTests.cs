using FluentAssertions;
using MailKit.Net.Imap;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Bundles;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSynchronizerLocalCalendarOperationTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private MailAccount _account = null!;
    private AccountCalendar _calendar = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _calendarService = new CalendarService(_databaseService);

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Local IMAP",
            Address = "local-imap@example.com",
            SenderName = "Local Imap User",
            ProviderType = MailProviderType.IMAP4,
            IsCalendarAccessGranted = true,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                IncomingServer = "imap.example.com",
                IncomingServerPort = "993",
                IncomingServerUsername = "local-imap@example.com",
                IncomingServerPassword = "password",
                OutgoingServer = "smtp.example.com",
                OutgoingServerPort = "587",
                OutgoingServerUsername = "local-imap@example.com",
                OutgoingServerPassword = "password",
                CalendarSupportMode = ImapCalendarSupportMode.LocalOnly
            }
        };

        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));

        _calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            Name = "Local Calendar",
            RemoteCalendarId = "local-primary",
            IsPrimary = true,
            TimeZone = "UTC",
            IsSynchronizationEnabled = true,
            BackgroundColorHex = "#0A84FF",
            TextColorHex = "#FFFFFF"
        };

        await _calendarService.InsertAccountCalendarAsync(_calendar);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task CreateCalendarEvent_LocalOnlyMode_StoresEventAndPersistsLocalIcs()
    {
        string capturedResourceHref = string.Empty;
        var changeProcessor = new Mock<IImapChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.SaveCalendarItemIcsAsync(
                _account.Id,
                _calendar.Id,
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Callback<Guid, Guid, Guid, string, string, string, string>((_, _, _, _, href, _, _) => capturedResourceHref = href)
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);

        try
        {
            var item = new CalendarItem
            {
                CalendarId = _calendar.Id,
                Title = "Planning",
                StartDate = new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc),
                DurationInSeconds = 1800,
                Description = "Sprint planning"
            };

            var request = new CreateCalendarEventRequest(item, []);
            var bundle = synchronizer.CreateCalendarEvent(request).Should().ContainSingle().Subject as ImapRequestBundle;

            bundle.Should().NotBeNull();
            bundle!.NativeRequest.RequiresConnectedClient.Should().BeFalse();

            await bundle.NativeRequest.IntegratorTask(Mock.Of<IImapClient>(), bundle.Request);

            var savedItem = await _calendarService.GetCalendarItemAsync(item.Id);
            savedItem.Should().NotBeNull();
            savedItem!.RemoteEventId.Should().StartWith("local-");
            savedItem.OrganizerEmail.Should().Be(_account.Address);
            savedItem.OrganizerDisplayName.Should().Be(_account.SenderName);

            capturedResourceHref.Should().Be($"local://calendar/{_calendar.Id:N}/{item.Id:N}");
            changeProcessor.VerifyAll();
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
        }
    }

    [Fact]
    public async Task AcceptEvent_LocalOnlyMode_UpdatesStatusAndPersistsIcs()
    {
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = _calendar.Id,
            Title = "Review",
            StartDate = new DateTime(2026, 3, 21, 9, 0, 0, DateTimeKind.Utc),
            DurationInSeconds = 3600,
            RemoteEventId = "local-existing",
            OrganizerDisplayName = _account.SenderName,
            OrganizerEmail = _account.Address,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC"
        };

        await _calendarService.CreateNewCalendarItemAsync(item, []);

        string capturedIcsContent = string.Empty;
        var changeProcessor = new Mock<IImapChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.SaveCalendarItemIcsAsync(
                _account.Id,
                _calendar.Id,
                item.Id,
                item.RemoteEventId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Callback<Guid, Guid, Guid, string, string, string, string>((_, _, _, _, _, _, ics) => capturedIcsContent = ics)
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);

        try
        {
            var request = new AcceptEventRequest(item);
            var bundle = synchronizer.AcceptEvent(request).Should().ContainSingle().Subject as ImapRequestBundle;

            bundle.Should().NotBeNull();
            await bundle!.NativeRequest.IntegratorTask(Mock.Of<IImapClient>(), bundle.Request);

            var savedItem = await _calendarService.GetCalendarItemAsync(item.Id);
            savedItem.Should().NotBeNull();
            savedItem!.Status.Should().Be(CalendarItemStatus.Accepted);

            capturedIcsContent.Should().Contain("STATUS:CONFIRMED");
            changeProcessor.VerifyAll();
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
        }
    }

    private ImapSynchronizer CreateSynchronizer(IImapChangeProcessor changeProcessor)
    {
        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupGet(x => x.ApplicationDataFolderPath).Returns(Path.GetTempPath());
        applicationConfiguration.SetupGet(x => x.PublisherSharedFolderPath).Returns(Path.GetTempPath());
        applicationConfiguration.SetupGet(x => x.ApplicationTempFolderPath).Returns(Path.GetTempPath());
        applicationConfiguration.SetupGet(x => x.SentryDNS).Returns(string.Empty);

        var unifiedSynchronizer = new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        return new ImapSynchronizer(
            _account,
            changeProcessor,
            applicationConfiguration.Object,
            unifiedSynchronizer,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            Mock.Of<ICalDavClient>(),
            Mock.Of<IAutoDiscoveryService>(),
            _calendarService);
    }
}
