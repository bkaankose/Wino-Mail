using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Tests.Synchronizers;
using Xunit;

namespace Wino.Core.Tests.Services;

public class AccountAttentionNetworkGateTests
{
    [Theory]
    [InlineData(AccountAttentionReason.None, false)]
    [InlineData(AccountAttentionReason.InvalidCredentials, true)]
    [InlineData(AccountAttentionReason.CertificateValidationFailed, true)]
    [InlineData(AccountAttentionReason.MissingSystemFolderConfiguration, true)]
    public void AnyAttentionReasonBlocksNetworkAccess(AccountAttentionReason reason, bool blocked)
    {
        new MailAccount { AttentionReason = reason }.IsNetworkAccessBlocked().Should().Be(blocked);
    }

    [Fact]
    public async Task PoolOpensNoConnectionForAccountThatNeedsAttention()
    {
        await using var server = new TestImapServer();
        var account = new MailAccount { Id = Guid.NewGuid(), AttentionReason = AccountAttentionReason.InvalidCredentials };
        await using var pool = new ImapClientPool(ImapClientPoolOptions.CreateDefault(server.Settings(max: 2), account: account));

        await pool.Invoking(p => p.InitializeAsync()).Should().ThrowAsync<AccountAttentionRequiredException>();
        await pool.Invoking(p => p.GetIdleClientAsync()).Should().ThrowAsync<AccountAttentionRequiredException>();
        await pool.Invoking(p => p.RentAsync()).Should().ThrowAsync<AccountAttentionRequiredException>();

        server.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task PoolRefusesLeasesAndClosesReturnedConnectionsOnceAttentionIsSet()
    {
        await using var server = new TestImapServer();
        var account = new MailAccount { Id = Guid.NewGuid() };
        await using var pool = new ImapClientPool(ImapClientPoolOptions.CreateDefault(server.Settings(max: 2), account: account));

        var leased = await pool.RentAsync();
        pool.Health.TotalConnections.Should().Be(1);

        account.AttentionReason = AccountAttentionReason.CertificateValidationFailed;
        pool.Return(leased);

        pool.Health.TotalConnections.Should().Be(0);
        await pool.Invoking(p => p.RentAsync()).Should().ThrowAsync<AccountAttentionRequiredException>();
        server.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public async Task SynchronizerForAccountThatNeedsAttentionStartsNoIdleUntilAttentionIsCleared()
    {
        await using var server = new TestImapServer { SupportsIdle = true };
        var account = CreateImapAccount(server, AccountAttentionReason.InvalidCredentials);
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(() => account);
        var factory = CreateFactory(accounts.Object);
        try
        {
            var synchronizer = factory.CreateNewSynchronizer(account);
            // The same instance the manager hands to callers must also refuse to start IDLE by itself.
            await ((IImapSynchronizer)synchronizer).StartIdleClientAsync();
            await Task.Delay(300);
            server.ConnectionCount.Should().Be(0);

            await factory.ApplyAccountAttentionAsync(CreateImapAccount(server, AccountAttentionReason.None, account.Id));

            await server.IdleReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            synchronizer.Account.AttentionReason.Should().Be(AccountAttentionReason.None);
        }
        finally { await factory.DeleteSynchronizerAsync(account.Id); }
    }

    [Fact]
    public async Task AttentionSetWhileIdleIsRunningStopsIdleAndClosesConnections()
    {
        await using var server = new TestImapServer { SupportsIdle = true };
        var account = CreateImapAccount(server, AccountAttentionReason.None);
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(() => account);
        var factory = CreateFactory(accounts.Object);
        try
        {
            var synchronizer = factory.CreateNewSynchronizer(account);
            await server.IdleReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var connectionsBeforeAttention = server.ConnectionCount;

            await factory.ApplyAccountAttentionAsync(CreateImapAccount(server, AccountAttentionReason.InvalidCredentials, account.Id))
                .WaitAsync(TimeSpan.FromSeconds(8));

            await server.DoneReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            synchronizer.Account.IsNetworkAccessBlocked().Should().BeTrue();

            // Restarting IDLE directly stays offline while the account needs attention.
            await ((IImapSynchronizer)synchronizer).StartIdleClientAsync();
            await Task.Delay(300);
            server.ConnectionCount.Should().Be(connectionsBeforeAttention);
        }
        finally { await factory.DeleteSynchronizerAsync(account.Id); }
    }

    private static MailAccount CreateImapAccount(TestImapServer server, AccountAttentionReason reason, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Attention",
        ProviderType = MailProviderType.IMAP4,
        ServerInformation = server.Settings(max: 2),
        AttentionReason = reason
    };

    private static SynchronizerFactory CreateFactory(IAccountService accounts)
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(Path.GetTempPath());
        configuration.SetupGet(x => x.PublisherSharedFolderPath).Returns(Path.GetTempPath());
        configuration.SetupGet(x => x.ApplicationTempFolderPath).Returns(Path.GetTempPath());
        return new SynchronizerFactory(Mock.Of<IOutlookChangeProcessor>(), Mock.Of<IGmailChangeProcessor>(),
            Mock.Of<IImapChangeProcessor>(), Mock.Of<IAuthenticationProvider>(), accounts, configuration.Object,
            Mock.Of<IOutlookSynchronizerErrorHandlerFactory>(), Mock.Of<IGmailSynchronizerErrorHandlerFactory>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>(), new UnifiedImapSynchronizer(Mock.Of<IFolderService>(),
                Mock.Of<IMailService>(), Mock.Of<IImapSynchronizerErrorHandlerFactory>()),
            Mock.Of<ICalDavClient>(), Mock.Of<IAutoDiscoveryService>(), Mock.Of<ICalendarService>(),
            Mock.Of<IMailCategoryService>(), Mock.Of<IMailFilterExecutor>(), Mock.Of<IServerCertificateTrustService>(),
            Mock.Of<IContactService>(), Mock.Of<IPictureStorageService>());
    }
}
