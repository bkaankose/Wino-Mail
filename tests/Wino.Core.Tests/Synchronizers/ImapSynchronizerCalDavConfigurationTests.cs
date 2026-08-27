using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSynchronizerCalDavConfigurationTests
{
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
                                                       IImapChangeProcessor? changeProcessor = null)
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
            Mock.Of<ICalendarService>());
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
