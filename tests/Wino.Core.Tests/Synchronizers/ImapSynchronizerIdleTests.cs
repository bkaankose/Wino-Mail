using FluentAssertions;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Server;
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
    public async Task FlagAndExpungeEventsDuringDebounceProduceTrailingSynchronization()
    {
        await using var server = new TestImapServer { SupportsIdle = true };
        var synchronizer = CreateSynchronizer(Path.GetTempPath(), server.Settings(max: 2));
        var recipient = new object();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(recipient, (_, message) =>
        {
            if (message.Options.AccountId != synchronizer.Account.Id) return;
            if (Interlocked.Increment(ref count) == 1) first.TrySetResult();
            else trailing.TrySetResult();
        });
        try
        {
            await synchronizer.StartIdleClientAsync();
            await server.IdleReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.IdleUpdates.Writer.WriteAsync("* 1 EXISTS");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.IdleUpdates.Writer.WriteAsync("* 1 FETCH (FLAGS (\\Seen))");
            await server.IdleUpdates.Writer.WriteAsync("* 1 EXPUNGE");
            await trailing.Task.WaitAsync(TimeSpan.FromSeconds(20));
            count.Should().Be(2);
        }
        finally
        {
            await synchronizer.KillSynchronizerAsync();
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task ConcurrentStartsUseOneListenerAndStopSendsDone()
    {
        await using var server = new TestImapServer { SupportsIdle = true };
        var synchronizer = CreateSynchronizer(Path.GetTempPath(), server.Settings(max: 2));
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => synchronizer.StartIdleClientAsync()));
            await server.IdleReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await synchronizer.StopIdleClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await server.DoneReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            server.Commands.Count(command => command == "IDLE").Should().Be(1);
            server.Commands.Should().NotContain("LOGOUT").And.NotContain("CLOSE");
        }
        finally { await synchronizer.KillSynchronizerAsync(); }
    }

    [Fact]
    public async Task ServerWithoutIdleOpensOnlyOneConnection()
    {
        await using var server = new TestImapServer();
        var synchronizer = CreateSynchronizer(Path.GetTempPath(), server.Settings(max: 2));
        try
        {
            await synchronizer.StartIdleClientAsync();
            await server.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await synchronizer.StopIdleClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            server.ConnectionCount.Should().Be(1);
            server.Commands.Should().NotContain("IDLE");
        }
        finally { await synchronizer.KillSynchronizerAsync(); }
    }

    private static ImapSynchronizer CreateSynchronizer(string appDataFolder, CustomServerInformation? settings = null)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "IMAP Test",
            Address = "test@example.com",
            ProviderType = MailProviderType.IMAP4,
            ServerInformation = settings ?? new CustomServerInformation
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
