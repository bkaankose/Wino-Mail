using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ModeSynchronizerFactoryTests
{
    [Fact]
    public async Task CalendarFactory_LegacyImapProviderSourceWithCalDavConfiguration_IsAvailable()
    {
        var account = CreateAccount(MailProviderType.IMAP4);
        account.IsCalendarAccessEnabled = true;
        account.IsCalendarAccessGranted = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        account.ServerInformation.CalendarSupportMode = ImapCalendarSupportMode.CalDav;
        account.ServerInformation.CalDavServiceUrl = "https://caldav.icloud.com/";
        var factory = CreateFactory(account);

        var strategy = await ((ICalendarSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);

        strategy.IsAvailable.Should().BeTrue();
    }

    [Theory]
    [InlineData(MailProviderType.Outlook, AccountIntegrationSource.Local, false, null, true)]
    [InlineData(MailProviderType.Outlook, AccountIntegrationSource.Provider, true, null, true)]
    [InlineData(MailProviderType.Outlook, AccountIntegrationSource.Provider, false, null, false)]
    [InlineData(MailProviderType.Exchange, AccountIntegrationSource.Provider, true, null, true)]
    [InlineData(MailProviderType.Exchange, AccountIntegrationSource.Provider, false, null, false)]
    [InlineData(MailProviderType.Exchange, AccountIntegrationSource.Local, false, null, true)]
    [InlineData(MailProviderType.Gmail, AccountIntegrationSource.Dav, false, "https://dav.example.test/", true)]
    [InlineData(MailProviderType.IMAP4, AccountIntegrationSource.Dav, false, null, false)]
    public async Task ContactFactory_UsesSelectedSourceWithoutLocalFallback(
        MailProviderType provider,
        AccountIntegrationSource source,
        bool grant,
        string? davUrl,
        bool expectedAvailable)
    {
        var account = CreateAccount(provider);
        account.ContactIntegrationSource = source;
        account.IsContactAccessGranted = grant;
        account.ServerInformation.CardDavServiceUrl = davUrl;
        var factory = CreateFactory(account);

        var strategy = await ((IContactSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);

        strategy.IsAvailable.Should().Be(expectedAvailable);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail, AccountIntegrationSource.Local, false, true)]
    [InlineData(MailProviderType.Gmail, AccountIntegrationSource.Provider, true, true)]
    [InlineData(MailProviderType.Gmail, AccountIntegrationSource.Provider, false, false)]
    [InlineData(MailProviderType.Exchange, AccountIntegrationSource.Provider, true, true)]
    [InlineData(MailProviderType.Exchange, AccountIntegrationSource.Provider, false, false)]
    [InlineData(MailProviderType.IMAP4, AccountIntegrationSource.Provider, true, false)]
    [InlineData(MailProviderType.IMAP4, AccountIntegrationSource.Dav, true, false)]
    public async Task TaskFactory_UsesSelectedSourceWithoutLocalFallback(
        MailProviderType provider,
        AccountIntegrationSource source,
        bool grant,
        bool expectedAvailable)
    {
        var account = CreateAccount(provider);
        account.TaskIntegrationSource = source;
        account.IsTaskAccessGranted = grant;
        var factory = CreateFactory(account);

        var strategy = await ((ITaskSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);

        strategy.IsAvailable.Should().Be(expectedAvailable);
    }

    [Fact]
    public async Task CalendarFactory_ExchangeProviderSource_IsAvailableWhenGranted()
    {
        var account = CreateAccount(MailProviderType.Exchange);
        account.IsCalendarAccessEnabled = true;
        account.IsCalendarAccessGranted = true;
        account.CalendarIntegrationSource = AccountIntegrationSource.Provider;
        var factory = CreateFactory(account);

        var strategy = await ((ICalendarSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);

        strategy.IsAvailable.Should().BeTrue();
        strategy.Capabilities.CanExecuteRequests.Should().BeTrue();
    }

    [Fact]
    public async Task ExchangeProviderSource_OffersNoCollectionManagement()
    {
        // Exchange serves one Contacts folder and one Tasks folder: no address books, lists, groups or steps.
        var account = CreateAccount(MailProviderType.Exchange);
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        account.IsContactAccessGranted = true;
        account.TaskIntegrationSource = AccountIntegrationSource.Provider;
        account.IsTaskAccessGranted = true;
        var factory = CreateFactory(account);

        var contacts = await ((IContactSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);
        var tasks = await ((ITaskSynchronizerFactory)factory).GetSynchronizerAsync(account.Id);

        contacts.IsAvailable.Should().BeTrue();
        contacts.Capabilities.CanManageAddressBooks.Should().BeFalse();
        tasks.IsAvailable.Should().BeTrue();
        tasks.Capabilities.CanManageLists.Should().BeFalse();
        tasks.Capabilities.CanManageSteps.Should().BeFalse();

        var outlook = CreateAccount(MailProviderType.Outlook);
        outlook.TaskIntegrationSource = AccountIntegrationSource.Provider;
        outlook.IsTaskAccessGranted = true;
        var outlookTasks = await ((ITaskSynchronizerFactory)CreateFactory(outlook)).GetSynchronizerAsync(outlook.Id);
        outlookTasks.Capabilities.CanManageLists.Should().BeTrue();
    }

    private static ModeSynchronizerFactory CreateFactory(MailAccount account)
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        var accountSynchronizer = new Mock<IWinoSynchronizerBase>();
        accountSynchronizer.SetupGet(synchronizer => synchronizer.Account).Returns(account);
        var synchronizerFactory = new Mock<ISynchronizerFactory>();
        synchronizerFactory.Setup(factory => factory.GetAccountSynchronizerAsync(account.Id))
            .ReturnsAsync(accountSynchronizer.Object);

        return new ModeSynchronizerFactory(accountService.Object, synchronizerFactory.Object);
    }

    private static MailAccount CreateAccount(MailProviderType provider)
        => new()
        {
            Id = Guid.NewGuid(),
            ProviderType = provider,
            IsContactAccessEnabled = true,
            IsTaskAccessEnabled = true,
            ServerInformation = new CustomServerInformation()
        };
}
