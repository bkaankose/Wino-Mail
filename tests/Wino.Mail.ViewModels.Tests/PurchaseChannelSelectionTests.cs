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
/// appears only when a store channel exists; App Store builds skip it outside the United States storefront.
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
    public async Task AccountManagement_AppleAppStore_UnitedStates_ShowsChoiceAndBuysFromAppStore()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.AppleAppStore);
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var appStore = CreateAppStore(externalPurchaseAllowed: true, WinoAppStorePurchaseOutcome.Cancelled);
        var vm = CreateAccountManagement(dialogs, store, profiles, new PlatformCapabilities(AppleAppStore: true), appStore.Object);

        await vm.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        appStore.Verify(a => a.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, It.IsAny<CancellationToken>()), Times.Once);
        store.VerifyNoOtherCalls();
        profiles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AccountManagement_AppleAppStore_OutsideUnitedStates_BuysFromAppStoreWithoutChoice()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var appStore = CreateAppStore(externalPurchaseAllowed: false, WinoAppStorePurchaseOutcome.Failed);
        var vm = CreateAccountManagement(dialogs, store, profiles, new PlatformCapabilities(AppleAppStore: true), appStore.Object);

        await vm.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
        appStore.Verify(a => a.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, It.IsAny<CancellationToken>()), Times.Once);
        dialogs.Verify(d => d.InfoBarMessage(It.IsAny<string>(), Translator.AppStorePurchase_Failed, InfoBarMessageType.Error), Times.Once);
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
    public async Task WinoAccountPage_AppleAppStoreChoice_BuysFromAppStore()
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.AppleAppStore);
        var profiles = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        var billing = new Mock<IWinoBillingService>(MockBehavior.Strict);
        var appStore = CreateAppStore(externalPurchaseAllowed: true, WinoAppStorePurchaseOutcome.Cancelled);
        var vm = CreateWinoAccountPage(dialogs, profiles, billing, new PlatformCapabilities(AppleAppStore: true), storeService: null, appStore.Object);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.UnlimitedAccountsAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Once);
        appStore.Verify(a => a.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, It.IsAny<CancellationToken>()), Times.Once);
        profiles.VerifyNoOtherCalls();
        billing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WinoAccountPage_AiPackOnAppStore_BuysFromAppStoreNotStripe()
    {
        var dialogs = new Mock<IMailDialogService>();
        var profiles = new Mock<IWinoAccountProfileService>();
        profiles.Setup(p => p.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoAccount { Id = Guid.NewGuid(), Email = "user@example.com" });
        var billing = new Mock<IWinoBillingService>(MockBehavior.Strict);
        var appStore = CreateAppStore(externalPurchaseAllowed: true, WinoAppStorePurchaseOutcome.Cancelled);
        var vm = CreateWinoAccountPage(dialogs, profiles, billing, new PlatformCapabilities(AppleAppStore: true), storeService: null, appStore.Object);

        await vm.PurchaseAddOnCommand.ExecuteAsync(vm.AiPackAddOn);

        dialogs.Verify(d => d.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
        appStore.Verify(a => a.PurchaseAsync(WinoAddOnProductType.AI_PACK, It.IsAny<CancellationToken>()), Times.Once);
        billing.VerifyNoOtherCalls();
    }

    private static Mock<IWinoAppStorePurchaseService> CreateAppStore(bool externalPurchaseAllowed, WinoAppStorePurchaseOutcome outcome)
    {
        var appStore = new Mock<IWinoAppStorePurchaseService>();
        appStore.SetupGet(a => a.IsAvailable).Returns(true);
        appStore.Setup(a => a.IsExternalPurchaseAllowedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(externalPurchaseAllowed);
        appStore.Setup(a => a.PurchaseAsync(It.IsAny<WinoAddOnProductType>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        return appStore;
    }

    private static AccountManagementViewModel CreateAccountManagement(Mock<IMailDialogService> dialogs,
        Mock<IMicrosoftStoreService> store, Mock<IWinoAccountProfileService> profiles, IPlatformCapabilities capabilities,
        IWinoAppStorePurchaseService? appStore = null)
        => new(dialogs.Object, Mock.Of<INavigationService>(), Mock.Of<IAccountService>(),
            Mock.Of<IKnownImapProviderCatalog>(), Mock.Of<IWinoBillingService>(), profiles.Object,
            Mock.Of<IWinoAccountDataSyncService>(), Mock.Of<IWinoLogger>(), Mock.Of<ISpecialImapProviderConfigResolver>(),
            Mock.Of<ICalDavClient>(), store.Object, Mock.Of<IAuthenticationProvider>(), Mock.Of<IPreferencesService>(),
            capabilities, appStore);

    private static WinoAccountManagementPageViewModel CreateWinoAccountPage(Mock<IMailDialogService> dialogs,
        Mock<IWinoAccountProfileService> profiles, Mock<IWinoBillingService> billing, IPlatformCapabilities capabilities,
        IMicrosoftStoreService? storeService, IWinoAppStorePurchaseService? appStore = null)
        => new(profiles.Object, dialogs.Object, billing.Object, Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IAccountService>(), Mock.Of<IMailIntelligenceCoordinator>(), Mock.Of<IPreferencesService>(),
            capabilities, Mock.Of<IExternalLauncher>(), storeService: storeService, appStorePurchases: appStore);
}
