using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// Unlimited Accounts can be bought through a store or through a Wino Account. The channel dialog
/// appears only when a store channel exists; the Apple App Store channel is not sold yet.
/// </summary>
public sealed class PurchaseChannelSelectionTests
{
    [Fact]
    public async Task AccountManagement_MicrosoftStore_ShowsChannelDialogAndBuysFromStore()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.MicrosoftStore);
        var store = new Mock<IMicrosoftStoreService>();
        store.Setup(s => s.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS))
            .ReturnsAsync(StorePurchaseResult.NotPurchased);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var vm = CreateAccountManagement(dialogs, store, profiles, new PlatformCapabilities(MicrosoftStore: true));

        await vm.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        store.Verify(s => s.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS), Times.Once);
        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccountManagement_NoStore_NeverShowsChannelDialog()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var profiles = new Mock<IWinoAccountProfileService>();
        profiles.Setup(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync((WinoAccount?)null);
        var vm = CreateAccountManagement(dialogs, store, profiles, new PlatformCapabilities());

        await vm.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
        profiles.Verify(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccountManagement_AppleAppStoreChoice_ShowsComingSoonOnly()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.AppleAppStore);
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var vm = CreateAccountManagement(dialogs, store, profiles, new PlatformCapabilities(AppleAppStore: true));

        await vm.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        dialogs.Verify(d => d.InfoBarMessage(It.IsAny<string>(),
            Translator.UnlimitedAccountsPurchaseDialog_AppleAppStoreComingSoon, InfoBarMessageType.Information), Times.Once);
        store.VerifyNoOtherCalls();
        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WinoAccountPage_NoStore_NeverShowsChannelDialog()
    {
        var dialogs = new Mock<IMailDialogService>();
        var profiles = new Mock<IWinoAccountProfileService>();
        profiles.Setup(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync((WinoAccount?)null);
        var billing = new Mock<IWinoBillingService>(MockBehavior.Strict);
        var vm = CreateWinoAccountPage(dialogs, profiles, billing, new PlatformCapabilities(), storeService: null);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.UnlimitedAccountsAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
        profiles.Verify(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()), Times.Once);
        billing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WinoAccountPage_MicrosoftStoreWithoutStoreService_NeverShowsChannelDialog()
    {
        var dialogs = new Mock<IMailDialogService>();
        var profiles = new Mock<IWinoAccountProfileService>();
        profiles.Setup(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync((WinoAccount?)null);
        var vm = CreateWinoAccountPage(dialogs, profiles, new Mock<IWinoBillingService>(MockBehavior.Strict),
            new PlatformCapabilities(MicrosoftStore: true), storeService: null);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.UnlimitedAccountsAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
    }

    [Fact]
    public async Task WinoAccountPage_MicrosoftStore_ShowsChannelDialogAndBuysFromStore()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.MicrosoftStore);
        var store = new Mock<IMicrosoftStoreService>();
        store.Setup(s => s.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS))
            .ReturnsAsync(StorePurchaseResult.NotPurchased);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var vm = CreateWinoAccountPage(dialogs, profiles, new Mock<IWinoBillingService>(MockBehavior.Strict),
            new PlatformCapabilities(MicrosoftStore: true), store.Object);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.UnlimitedAccountsAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        store.Verify(s => s.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS), Times.Once);
        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WinoAccountPage_AppleAppStoreChoice_ShowsComingSoonOnly()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.AppleAppStore);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var billing = new Mock<IWinoBillingService>(MockBehavior.Strict);
        var vm = CreateWinoAccountPage(dialogs, profiles, billing, new PlatformCapabilities(AppleAppStore: true), storeService: null);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.UnlimitedAccountsAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        dialogs.Verify(d => d.InfoBarMessage(It.IsAny<string>(),
            Translator.UnlimitedAccountsPurchaseDialog_AppleAppStoreComingSoon, InfoBarMessageType.Information), Times.Once);
        profiles.VerifyNoOtherCalls();
        billing.VerifyNoOtherCalls();
    }

    private static AccountManagementViewModel CreateAccountManagement(Mock<IMailDialogService> dialogs,
        Mock<IMicrosoftStoreService> store, Mock<IWinoAccountProfileService> profiles, IPlatformCapabilities capabilities)
        => new(dialogs.Object, Mock.Of<INavigationService>(), Mock.Of<IAccountService>(),
            Mock.Of<IKnownImapProviderCatalog>(), Mock.Of<IWinoBillingService>(), profiles.Object,
            Mock.Of<IWinoAccountDataSyncService>(), Mock.Of<IWinoLogger>(), Mock.Of<ISpecialImapProviderConfigResolver>(),
            Mock.Of<ICalDavClient>(), store.Object, Mock.Of<IAuthenticationProvider>(), Mock.Of<IPreferencesService>(),
            capabilities);

    private static WinoAccountManagementPageViewModel CreateWinoAccountPage(Mock<IMailDialogService> dialogs,
        Mock<IWinoAccountProfileService> profiles, Mock<IWinoBillingService> billing, IPlatformCapabilities capabilities,
        IMicrosoftStoreService? storeService)
        => new(profiles.Object, dialogs.Object, billing.Object, Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IAccountService>(), Mock.Of<IMailIntelligenceCoordinator>(), Mock.Of<IPreferencesService>(),
            capabilities, storeService: storeService);
}
