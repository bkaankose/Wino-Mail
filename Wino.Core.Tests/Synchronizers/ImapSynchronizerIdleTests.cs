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

public class ImapSynchronizerIdleTests
{
    [Fact]
    public async Task ShouldTriggerIdleSynchronization_ShouldDebounceBurstSignals()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "wino-imap-idle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var synchronizer = CreateSynchronizer(tempDirectory);

        try
        {
            var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            synchronizer.ShouldTriggerIdleSynchronization(baseTime).Should().BeTrue();
            synchronizer.ShouldTriggerIdleSynchronization(baseTime.AddSeconds(5)).Should().BeFalse();
            synchronizer.ShouldTriggerIdleSynchronization(baseTime.AddSeconds(16)).Should().BeTrue();
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static ImapSynchronizer CreateSynchronizer(string appDataFolder)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Test",
            Address = "test@example.com",
            ProviderType = MailProviderType.IMAP4,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                IncomingServer = "imap.example.com",
                IncomingServerPort = "993",
                IncomingServerUsername = "user",
                IncomingServerPassword = "password",
                MaxConcurrentClients = 5
            }
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
            Mock.Of<IAutoDiscoveryService>(),
            Mock.Of<ICalendarService>());
    }
}
