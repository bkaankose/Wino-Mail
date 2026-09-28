using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Services;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Tests.Synchronizers;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SynchronizerFactoryConcurrencyTests
{
    [Fact]
    public async Task ConcurrentLookupsShareOneSynchronizerAndReplacementDisposesOldPool()
    {
        await using var server = new TestImapServer();
        var account = new MailAccount { Id = Guid.NewGuid(), Name = "Test", ProviderType = MailProviderType.IMAP4, ServerInformation = server.Settings() };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(() => account);
        var factory = CreateFactory(accounts.Object);
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => factory.GetAccountSynchronizerAsync(account.Id)));
            var synchronizers = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            synchronizers.Distinct().Should().ContainSingle();
            factory.CreateNewSynchronizer(account).Should().BeSameAs(synchronizers[0]);
            factory.CachedSynchronizers.Should().ContainSingle();
            var old = synchronizers[0];
            account = new MailAccount { Id = account.Id, Name = "Test", ProviderType = MailProviderType.IMAP4, ServerInformation = server.Settings(), IsProtocolLogEnabled = true };
            var replacement = await factory.GetAccountSynchronizerAsync(account.Id);
            replacement.Should().NotBeSameAs(old);
            factory.CachedSynchronizers[account.Id].Should().BeSameAs(replacement);
            await factory.DeleteSynchronizerAsync(account.Id);
            factory.CachedSynchronizers.Should().BeEmpty();
        }
        finally { await factory.DeleteSynchronizerAsync(account.Id); }
    }

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
