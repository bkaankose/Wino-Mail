using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Core.Tests.ViewModels;

public sealed class WinoAccountUnlimitedAccountsPurchaseChannelTests
{
    [Fact]
    public async Task UnlimitedAccounts_MicrosoftStoreSelected_UsesStorePurchase()
    {
        var context = CreateContext(UnlimitedAccountsPurchaseChannel.MicrosoftStore);
        context.Store.Setup(x => x.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS))
            .ReturnsAsync(StorePurchaseResult.Succeeded);

        await context.ViewModel.PurchaseAddOnCommand.ExecuteAsync(context.ViewModel.UnlimitedAccountsAddOn);

        context.Store.Verify(x => x.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS), Times.Once);
        context.Billing.Verify(x => x.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Profile.Verify(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnlimitedAccounts_WinoAccountSelected_UsesStripeCheckout()
    {
        var context = CreateContext(UnlimitedAccountsPurchaseChannel.WinoAccount);

        await context.ViewModel.PurchaseAddOnCommand.ExecuteAsync(context.ViewModel.UnlimitedAccountsAddOn);

        context.Billing.Verify(x => x.OpenCheckoutAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS, It.IsAny<CancellationToken>()), Times.Once);
        context.Store.Verify(x => x.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
    }

    [Fact]
    public async Task UnlimitedAccounts_ChannelDialogCancelled_StartsNoPurchase()
    {
        var context = CreateContext(null);

        await context.ViewModel.PurchaseAddOnCommand.ExecuteAsync(context.ViewModel.UnlimitedAccountsAddOn);

        context.Store.Verify(x => x.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
        context.Billing.Verify(x => x.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AiPack_SkipsChannelDialogAndUsesStripeCheckout()
    {
        var context = CreateContext(UnlimitedAccountsPurchaseChannel.MicrosoftStore);

        await context.ViewModel.PurchaseAddOnCommand.ExecuteAsync(context.ViewModel.AiPackAddOn);

        context.Dialogs.Verify(x => x.ShowUnlimitedAccountsPurchaseChannelDialogAsync(), Times.Never);
        context.Billing.Verify(x => x.OpenCheckoutAsync(WinoAddOnProductType.AI_PACK, It.IsAny<CancellationToken>()), Times.Once);
        context.Store.Verify(x => x.PurchaseAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
    }

    [Fact]
    public void SignedOutUnlimitedAccountsBenefit_MentionsMicrosoftStore()
    {
        var context = CreateContext(null);

        context.ViewModel.Benefits
            .Single(x => x.Type == WinoAccountBenefitType.UnlimitedAccounts)
            .Points.Should().Contain(Translator.WinoAccount_Management_Benefit_Unlimited_Point5);
    }

    private static TestContext CreateContext(UnlimitedAccountsPurchaseChannel? channel)
    {
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(x => x.ShowUnlimitedAccountsPurchaseChannelDialogAsync()).ReturnsAsync(channel);
        var store = new Mock<IMicrosoftStoreService>();
        var billing = new Mock<IWinoBillingService>();
        billing.Setup(x => x.OpenCheckoutAsync(It.IsAny<WinoAddOnProductType>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoAccount { Id = Guid.NewGuid(), Email = "buyer@example.test" });
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync([]);

        var viewModel = new WinoAccountManagementPageViewModel(
            profile.Object,
            Mock.Of<IWinoAccountDataSyncService>(),
            dialogs.Object,
            billing.Object,
            Mock.Of<IWinoAccountApiClient>(),
            accounts.Object,
            Mock.Of<IMailIntelligenceCoordinator>(),
            Mock.Of<IPreferencesService>(),
            storeService: store.Object);

        return new TestContext(viewModel, dialogs, store, billing, profile);
    }

    private sealed record TestContext(
        WinoAccountManagementPageViewModel ViewModel,
        Mock<IMailDialogService> Dialogs,
        Mock<IMicrosoftStoreService> Store,
        Mock<IWinoBillingService> Billing,
        Mock<IWinoAccountProfileService> Profile);
}
