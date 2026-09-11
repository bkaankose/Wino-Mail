using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class AccountManagementPurchaseChannelTests
{
    [Fact]
    public async Task Upgrade_Cancelled_DoesNotStartPurchase()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IStoreManagementService>();
        var billing = new Mock<IWinoBillingService>();
        dialogs.Setup(service => service.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync((UnlimitedAccountsPurchaseChannel?)null);
        var viewModel = CreateViewModel(dialogs, store, billing);

        await viewModel.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        store.Verify(service => service.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
        billing.Verify(service => service.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), default), Times.Never);
    }

    [Fact]
    public async Task Upgrade_MicrosoftStoreSelected_UsesStorePurchase()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IStoreManagementService>();
        var billing = new Mock<IWinoBillingService>();
        var profile = new Mock<IWinoAccountProfileService>();
        dialogs.Setup(service => service.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.MicrosoftStore);
        store.Setup(service => service.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS))
            .ReturnsAsync(StorePurchaseResult.Succeeded);
        billing.Setup(service => service.HasUnlimitedAccountsAsync(default)).ReturnsAsync(true);
        var viewModel = CreateViewModel(dialogs, store, billing, profile);

        await viewModel.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        store.Verify(service => service.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS), Times.Once);
        billing.Verify(service => service.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), default), Times.Never);
        profile.Verify(service => service.GetAuthenticatedAccountAsync(default), Times.Never);
        viewModel.HasUnlimitedAccountProduct.Should().BeTrue();
    }

    [Fact]
    public async Task Upgrade_WinoAccountSelected_UsesStripeCheckout()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IStoreManagementService>();
        var billing = new Mock<IWinoBillingService>();
        var profile = new Mock<IWinoAccountProfileService>();
        dialogs.Setup(service => service.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.WinoAccount);
        profile.Setup(service => service.GetAuthenticatedAccountAsync(default))
            .ReturnsAsync(new WinoAccount());
        billing.Setup(service => service.OpenCheckoutAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, default))
            .ReturnsAsync(true);
        var viewModel = CreateViewModel(dialogs, store, billing, profile);

        await viewModel.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        billing.Verify(service => service.OpenCheckoutAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, default), Times.Once);
        store.Verify(service => service.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
    }

    [Fact]
    public async Task Upgrade_WinoAccountSelectedWhileSignedOut_ShowsSignInMessage()
    {
        var dialogs = new Mock<IMailDialogService>();
        var store = new Mock<IStoreManagementService>();
        var billing = new Mock<IWinoBillingService>();
        var profile = new Mock<IWinoAccountProfileService>();
        dialogs.Setup(service => service.ShowUnlimitedAccountsPurchaseChannelDialogAsync())
            .ReturnsAsync(UnlimitedAccountsPurchaseChannel.WinoAccount);
        profile.Setup(service => service.GetAuthenticatedAccountAsync(default))
            .ReturnsAsync((WinoAccount?)null);
        var viewModel = CreateViewModel(dialogs, store, billing, profile);

        await viewModel.PurchaseUnlimitedAccountCommand.ExecuteAsync(null);

        dialogs.Verify(service => service.InfoBarMessage(
            It.IsAny<string>(),
            It.IsAny<string>(),
            InfoBarMessageType.Warning), Times.Once);
        billing.Verify(service => service.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), default), Times.Never);
        store.Verify(service => service.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
    }

    private static AccountManagementViewModel CreateViewModel(
        Mock<IMailDialogService> dialogs,
        Mock<IStoreManagementService> store,
        Mock<IWinoBillingService> billing,
        Mock<IWinoAccountProfileService>? profile = null)
        => new(
            dialogs.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IProviderService>(),
            billing.Object,
            profile?.Object ?? Mock.Of<IWinoAccountProfileService>(),
            Mock.Of<IWinoAccountDataSyncService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<ISpecialImapProviderConfigResolver>(),
            Mock.Of<ICalDavClient>(),
            store.Object,
            Mock.Of<IAuthenticationProvider>(),
            Mock.Of<IPreferencesService>());
}
