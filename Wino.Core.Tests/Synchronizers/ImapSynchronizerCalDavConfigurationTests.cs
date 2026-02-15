using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSynchronizerCalDavConfigurationTests
{
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
                                                       IAutoDiscoveryService autoDiscoveryService = null)
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
            Mock.Of<IImapChangeProcessor>(),
            applicationConfiguration.Object,
            unifiedSynchronizer,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(),
            Mock.Of<ICalDavClient>(),
            autoDiscoveryService ?? Mock.Of<IAutoDiscoveryService>());
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
