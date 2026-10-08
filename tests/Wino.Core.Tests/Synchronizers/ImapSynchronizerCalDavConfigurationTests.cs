using System.Reflection;
using FluentAssertions;
using Moq;
using Itenso.TimePeriod;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSynchronizerCalDavConfigurationTests
{
    [Fact]
    public async Task CalendarSync_LegacyTokenAndSelfLinkedOccurrence_ReimportsAndRepairsOnce()
    {
        var tempDirectory = CreateTempDirectory();
        var serverInformation = CreateServerInformation();
        serverInformation.CalDavServiceUrl = "https://dav.example.test/";
        var remoteCalendar = new CalDavCalendar
        {
            RemoteCalendarId = "https://dav.example.test/calendars/default/",
            Name = "Calendar",
            SyncToken = "sync-1",
            CTag = "ctag-1"
        };
        var localCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            RemoteCalendarId = remoteCalendar.RemoteCalendarId,
            Name = remoteCalendar.Name,
            IsSynchronizationEnabled = true,
            SynchronizationDeltaToken = "sync-1|ctag-1"
        };
        var parent = new CalendarItem { Id = Guid.NewGuid(), RemoteEventId = "series", Recurrence = "RRULE:FREQ=DAILY" };
        var childId = Guid.NewGuid();
        var corruptedChild = new CalendarItem
        {
            Id = childId,
            RemoteEventId = "series::20260914T100000Z",
            RecurringCalendarItemId = childId
        };
        var remoteChild = new CalDavCalendarEvent
        {
            RemoteEventId = corruptedChild.RemoteEventId,
            SeriesMasterRemoteEventId = parent.RemoteEventId,
            IsRecurringInstance = true,
            ETag = "\"unchanged\""
        };
        var calDavClient = new Mock<ICalDavClient>();
        calDavClient.Setup(client => client.DiscoverCalendarsAsync(It.IsAny<CalDavConnectionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { remoteCalendar });
        calDavClient.Setup(client => client.GetCalendarEventsAsync(
                It.IsAny<CalDavConnectionSettings>(), remoteCalendar, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { remoteChild });
        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor.Setup(processor => processor.GetAccountCalendarsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccountCalendar> { localCalendar });
        changeProcessor.Setup(processor => processor.GetCalendarItemAsync(localCalendar.Id, corruptedChild.RemoteEventId))
            .ReturnsAsync(corruptedChild);
        changeProcessor.Setup(processor => processor.GetCalendarItemAsync(localCalendar.Id, parent.RemoteEventId))
            .ReturnsAsync(parent);
        changeProcessor.Setup(processor => processor.GetCalendarItemIcsETagAsync(It.IsAny<Guid>(), localCalendar.Id, corruptedChild.Id))
            .ReturnsAsync(remoteChild.ETag);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync(new List<CalendarItem>());
        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation,
            configureAccount: account => account.CalendarIntegrationSource = AccountIntegrationSource.Dav,
            calDavClient: calDavClient.Object, changeProcessor: changeProcessor.Object, calendarService: calendarService.Object);

        try
        {
            var options = new CalendarSynchronizationOptions { Type = CalendarSynchronizationType.CalendarEvents };
            (await synchronizer.SynchronizeCalendarEventsAsync(options)).CompletedState.Should().Be(SynchronizationCompletedState.Success);
            (await synchronizer.SynchronizeCalendarEventsAsync(options)).CompletedState.Should().Be(SynchronizationCompletedState.Success);

            calDavClient.Verify(client => client.GetCalendarEventsAsync(
                It.IsAny<CalDavConnectionSettings>(), remoteCalendar, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            changeProcessor.Verify(processor => processor.ManageCalendarEventAsync(remoteChild, localCalendar, synchronizer.Account), Times.Once);
            localCalendar.SynchronizationDeltaToken.Should().NotBe("sync-1|ctag-1");
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task CalendarSync_WindowEndApproaching_ReanchorsOnceAndPrunesExpiredOccurrences()
    {
        var tempDirectory = CreateTempDirectory();
        var serverInformation = CreateServerInformation();
        serverInformation.CalDavServiceUrl = "https://dav.example.test/";
        var remoteCalendar = new CalDavCalendar
        {
            RemoteCalendarId = "https://dav.example.test/calendars/default/",
            Name = "Calendar",
            SyncToken = "sync-1",
            CTag = "ctag-1"
        };
        var localCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            RemoteCalendarId = remoteCalendar.RemoteCalendarId,
            Name = remoteCalendar.Name,
            IsSynchronizationEnabled = true,
            // Same server token as before, but the tracked window ends in 100 days.
            SynchronizationDeltaToken = CalendarSyncWindowToken.Encode("sync-1|ctag-1", DateTimeOffset.UtcNow.AddDays(100))
        };
        var parent = new CalendarItem { Id = Guid.NewGuid(), CalendarId = localCalendar.Id, RemoteEventId = "series", Recurrence = "RRULE:FREQ=DAILY" };
        var remoteChild = new CalDavCalendarEvent
        {
            RemoteEventId = "series::20260914T100000Z",
            SeriesMasterRemoteEventId = parent.RemoteEventId,
            IsRecurringInstance = true,
            ETag = "\"unchanged\""
        };
        var expiredChild = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = localCalendar.Id,
            RemoteEventId = "series::20230914T100000Z",
            RecurringCalendarItemId = parent.Id,
            StartDate = DateTime.UtcNow.AddYears(-3),
            DurationInSeconds = 3600,
            StartTimeZone = "UTC",
            EndTimeZone = "UTC"
        };
        var calDavClient = new Mock<ICalDavClient>();
        calDavClient.Setup(client => client.DiscoverCalendarsAsync(It.IsAny<CalDavConnectionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { remoteCalendar });
        calDavClient.Setup(client => client.GetCalendarEventsAsync(
                It.IsAny<CalDavConnectionSettings>(), remoteCalendar, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { remoteChild });
        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor.Setup(processor => processor.GetAccountCalendarsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<AccountCalendar> { localCalendar });
        changeProcessor.Setup(processor => processor.GetCalendarItemAsync(localCalendar.Id, parent.RemoteEventId))
            .ReturnsAsync(parent);
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            // The database only returns the expired child for the open-ended "before the window" query.
            .ReturnsAsync((IAccountCalendar _, ITimePeriod period) =>
                period.Start == DateTime.MinValue ? new List<CalendarItem> { expiredChild } : new List<CalendarItem>());
        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation,
            configureAccount: account => account.CalendarIntegrationSource = AccountIntegrationSource.Dav,
            calDavClient: calDavClient.Object, changeProcessor: changeProcessor.Object, calendarService: calendarService.Object);

        try
        {
            var options = new CalendarSynchronizationOptions { Type = CalendarSynchronizationType.CalendarEvents };
            (await synchronizer.SynchronizeCalendarEventsAsync(options)).CompletedState.Should().Be(SynchronizationCompletedState.Success);

            var (providerToken, windowEnd) = CalendarSyncWindowToken.Decode(localCalendar.SynchronizationDeltaToken);
            providerToken.Should().Be("sync-1|ctag-1");
            windowEnd!.Value.Date.Should().Be(DateTimeOffset.UtcNow.AddYears(2).Date, "the calendar is re-anchored to a fresh window");
            changeProcessor.Verify(processor => processor.DeleteCalendarItemAsync(expiredChild.Id), Times.Once, "occurrences before the window are pruned");

            (await synchronizer.SynchronizeCalendarEventsAsync(options)).CompletedState.Should().Be(SynchronizationCompletedState.Success);

            calDavClient.Verify(client => client.GetCalendarEventsAsync(
                It.IsAny<CalDavConnectionSettings>(), remoteCalendar, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once,
                "an unchanged server token with a fresh window skips the calendar");
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_Occurrence_RefreshesSnapshotOfEverySeriesRow()
    {
        const string seriesIcs = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//Tests//EN
            BEGIN:VEVENT
            UID:series
            DTSTAMP:20260201T000000Z
            DTSTART:20260219T100000Z
            DTEND:20260219T110000Z
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Series
            END:VEVENT
            END:VCALENDAR
            """;
        const string resourceHref = "https://dav.example.test/calendars/default/series.ics";

        var tempDirectory = CreateTempDirectory();
        var serverInformation = CreateServerInformation();
        serverInformation.CalDavServiceUrl = "https://dav.example.test/";
        serverInformation.CalDavPassword = "test-password";
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            RemoteCalendarId = "https://dav.example.test/calendars/default/",
            Name = "Calendar",
            IsSynchronizationEnabled = true
        };
        var parent = new CalendarItem { Id = Guid.NewGuid(), CalendarId = calendar.Id, RemoteEventId = "series", Recurrence = "RRULE:FREQ=DAILY;COUNT=3" };
        var deleted = new CalendarItem { Id = Guid.NewGuid(), CalendarId = calendar.Id, RemoteEventId = "series::20260220T100000Z", RecurringCalendarItemId = parent.Id };
        var sibling = new CalendarItem { Id = Guid.NewGuid(), CalendarId = calendar.Id, RemoteEventId = "series::20260221T100000Z", RecurringCalendarItemId = parent.Id };

        var calDavClient = new Mock<ICalDavClient>();
        calDavClient.Setup(client => client.UpsertCalendarEventAsync(
                It.IsAny<CalDavConnectionSettings>(), It.IsAny<CalDavCalendar>(), It.IsAny<CalDavWriteRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalDavWriteResult { ExactHref = resourceHref, ETag = "\"v2\"" });
        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor.Setup(processor => processor.GetCalendarItemIcsAsync(It.IsAny<Guid>(), calendar.Id, deleted.Id))
            .ReturnsAsync(new CalDavResourceSnapshot { ExactHref = resourceHref, ETag = "\"v1\"", IcsContent = seriesIcs });
        var calendarService = new Mock<ICalendarService>();
        calendarService.Setup(service => service.GetAccountCalendarAsync(calendar.Id)).ReturnsAsync(calendar);
        calendarService.Setup(service => service.GetCalendarItemAsync(parent.Id)).ReturnsAsync(parent);
        calendarService.Setup(service => service.GetRecurringChildrenAsync(parent.Id)).ReturnsAsync(new List<CalendarItem> { deleted, sibling });
        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation,
            configureAccount: account => account.CalendarIntegrationSource = AccountIntegrationSource.Dav,
            calDavClient: calDavClient.Object, changeProcessor: changeProcessor.Object, calendarService: calendarService.Object);

        try
        {
            var handler = InvokePrivate<object>(synchronizer, "ResolveCalendarOperationHandler");
            var method = handler.GetType().GetMethod("DeleteCalendarEventAsync", BindingFlags.Public | BindingFlags.Instance)
                         ?? throw new InvalidOperationException("DeleteCalendarEventAsync not found.");

            await (Task)method.Invoke(handler, [new DeleteCalendarEventRequest(deleted)])!;

            calDavClient.Verify(client => client.UpsertCalendarEventAsync(
                It.IsAny<CalDavConnectionSettings>(), It.IsAny<CalDavCalendar>(),
                It.Is<CalDavWriteRequest>(request => request.ExactHref == resourceHref && request.ETag == "\"v1\"" && request.IcsContent.Contains("EXDATE")),
                It.IsAny<CancellationToken>()), Times.Once);

            foreach (var row in new[] { parent, deleted, sibling })
            {
                changeProcessor.Verify(processor => processor.SaveCalendarItemIcsAsync(
                    synchronizer.Account.Id, calendar.Id, row.Id, row.RemoteEventId, resourceHref, "\"v2\"", It.Is<string>(ics => ics.Contains("EXDATE"))),
                    Times.Once, $"row {row.RemoteEventId} must carry the new ETag");
            }

            changeProcessor.Verify(processor => processor.SaveCalendarItemIcsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Exactly(3));
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task LegacyProviderCalendarSource_WithCalDavSupport_UsesCalDavForSyncAndRequests()
    {
        var tempDirectory = CreateTempDirectory();
        var serverInformation = CreateServerInformation();
        serverInformation.CalDavServiceUrl = "https://caldav.icloud.com/";
        var calDavClient = new Mock<ICalDavClient>();
        calDavClient
            .Setup(client => client.DiscoverCalendarsAsync(
                It.IsAny<CalDavConnectionSettings>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var changeProcessor = new Mock<IImapChangeProcessor>();
        changeProcessor
            .Setup(processor => processor.GetAccountCalendarsAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        var synchronizer = CreateSynchronizer(
            tempDirectory,
            serverInformation,
            configureAccount: account =>
            {
                account.SpecialImapProvider = SpecialImapProvider.iCloud;
                account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
            },
            calDavClient: calDavClient.Object,
            changeProcessor: changeProcessor.Object);

        try
        {
            synchronizer.Account.GetEffectiveCalendarIntegrationSource().Should().Be(AccountIntegrationSource.Dav);

            var handler = InvokePrivate<object>(synchronizer, "ResolveCalendarOperationHandler");
            var result = await synchronizer.SynchronizeCalendarEventsAsync(new()
            {
                Type = CalendarSynchronizationType.CalendarMetadata
            });

            handler.GetType().Name.Should().Be("CalDavCalendarOperationHandler");
            result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
            calDavClient.Verify(client => client.DiscoverCalendarsAsync(
                It.IsAny<CalDavConnectionSettings>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ResolveCalDavServiceUriAsync_UsesExplicitConfigurationBeforeAutoDiscovery()
    {
        var tempDirectory = CreateTempDirectory();
        var autoDiscovery = new Mock<IAutoDiscoveryService>(MockBehavior.Strict);

        var serverInformation = CreateServerInformation();
        serverInformation.CalDavServiceUrl = "https://caldav.explicit.example.com/";

        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation, autoDiscovery.Object);

        try
        {
            var resolvedUri = await InvokePrivateAsync<Uri>(synchronizer, "ResolveCalDavServiceUriAsync", CancellationToken.None);

            resolvedUri.Should().Be(new Uri("https://caldav.explicit.example.com/"));
            autoDiscovery.Verify(a => a.DiscoverCalDavServiceUriAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ResolveCalDavPassword_PrefersExplicitCalDavPassword()
    {
        var tempDirectory = CreateTempDirectory();

        var serverInformation = CreateServerInformation();
        serverInformation.IncomingServerPassword = "incoming-password";
        serverInformation.OutgoingServerPassword = "outgoing-password";
        serverInformation.CalDavPassword = "caldav-password";

        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation);

        try
        {
            var password = InvokePrivate<string>(synchronizer, "ResolveCalDavPassword");

            password.Should().Be("caldav-password");
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ResolveCalDavUsername_PrefersExplicitCalDavUsername()
    {
        var tempDirectory = CreateTempDirectory();

        var serverInformation = CreateServerInformation();
        serverInformation.Address = "fallback@example.com";
        serverInformation.CalDavUsername = "calendar-user@example.com";

        var synchronizer = CreateSynchronizer(tempDirectory, serverInformation);

        try
        {
            var username = InvokePrivate<string>(synchronizer, "ResolveCalDavUsername");

            username.Should().Be("calendar-user@example.com");
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            DeleteDirectory(tempDirectory);
        }
    }

    private static ImapSynchronizer CreateSynchronizer(string appDataFolder,
                                                       CustomServerInformation serverInformation,
                                                       IAutoDiscoveryService? autoDiscoveryService = null,
                                                       Action<MailAccount>? configureAccount = null,
                                                       ICalDavClient? calDavClient = null,
                                                       IImapChangeProcessor? changeProcessor = null,
                                                       ICalendarService? calendarService = null)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Test",
            Address = "test@example.com",
            ProviderType = MailProviderType.IMAP4,
            IsCalendarAccessGranted = true,
            ServerInformation = serverInformation
        };

        configureAccount?.Invoke(account);

        var applicationConfiguration = new Mock<IApplicationConfiguration>();
        applicationConfiguration.SetupProperty(x => x.ApplicationDataFolderPath, appDataFolder);
        applicationConfiguration.SetupProperty(x => x.PublisherSharedFolderPath, appDataFolder);
        applicationConfiguration.SetupProperty(x => x.ApplicationTempFolderPath, appDataFolder);
        applicationConfiguration.SetupGet(x => x.SentryDNS).Returns(string.Empty);

        var unifiedSynchronizer = new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        return new ImapSynchronizer(
            account,
            changeProcessor ?? Mock.Of<IImapChangeProcessor>(),
            applicationConfiguration.Object,
            unifiedSynchronizer,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            calDavClient ?? Mock.Of<ICalDavClient>(),
            autoDiscoveryService ?? Mock.Of<IAutoDiscoveryService>(),
            calendarService ?? Mock.Of<ICalendarService>());
    }

    private static CustomServerInformation CreateServerInformation()
        => new()
        {
            Id = Guid.NewGuid(),
            IncomingServer = "imap.example.com",
            IncomingServerPort = "993",
            IncomingServerUsername = "user@example.com",
            IncomingServerPassword = "password",
            OutgoingServer = "smtp.example.com",
            OutgoingServerPort = "587",
            OutgoingServerUsername = "user@example.com",
            OutgoingServerPassword = "password",
            MaxConcurrentClients = 5,
            CalendarSupportMode = ImapCalendarSupportMode.CalDav
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wino-imap-caldav-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static T InvokePrivate<T>(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        return (T)method.Invoke(instance, null)!;
    }

    private static async Task<T> InvokePrivateAsync<T>(object instance, string methodName, params object[] parameters)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var task = (Task<T>)method.Invoke(instance, parameters)!;
        return await task.ConfigureAwait(false);
    }
}
