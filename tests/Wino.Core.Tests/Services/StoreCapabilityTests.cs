using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Tests.Helpers;
using Wino.Core.ViewModels;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class StoreCapabilityTests : IAsyncLifetime
{
    private InMemoryDatabaseService _database = null!;

    public async Task InitializeAsync()
    {
        _database = new InMemoryDatabaseService();
        await _database.Connection.CreateTableAsync<WinoAccount>();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnlimitedAccounts_WhenStoreUnavailable_PreservesAccountEntitlementWithoutCallingStore(bool purchased)
    {
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        await _database.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(), IsUnlimitedAccountsEnabled = purchased,
        });
        var billing = new WinoBillingService(_database, Mock.Of<IWinoAccountApiClient>(),
            store.Object, Mock.Of<IExternalLauncher>(), new PlatformCapabilities());

        Assert.Equal(purchased, await billing.HasUnlimitedAccountsAsync());
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnlimitedAccounts_WhenStoreAvailable_UsesExistingStoreLicenseFallback()
    {
        var store = new Mock<IMicrosoftStoreService>();
        store.Setup(value => value.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)).ReturnsAsync(true);
        var billing = new WinoBillingService(_database, Mock.Of<IWinoAccountApiClient>(),
            store.Object, Mock.Of<IExternalLauncher>(), new PlatformCapabilities(MicrosoftStore: true));

        Assert.True(await billing.HasUnlimitedAccountsAsync());
        store.Verify(value => value.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS), Times.Once);
    }

    [Fact]
    public async Task StoreRedemption_WhenUnavailable_ProducesNoCandidateOrExternalEffects()
    {
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var api = new Mock<IWinoAccountApiClient>(MockBehavior.Strict);
        var settings = new Mock<IConfigurationService>(MockBehavior.Strict);
        var redeem = new WinoStorePurchaseRedeemService(_database, api.Object, store.Object,
            settings.Object, new PlatformCapabilities());

        Assert.Null(await redeem.GetRedeemCandidateAsync());
        Assert.Equal(WinoStorePurchaseRedeemOutcome.Unavailable,
            await redeem.RedeemUnlimitedAccountsAsync(new WinoStoreRedeemCandidate("user-hash", "ticket")));

        store.VerifyNoOtherCalls();
        api.VerifyNoOtherCalls();
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StorePurchase_WhenUnavailable_RejectsWithoutOpeningStoreOrShowingPurchaseFailure()
    {
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var dialog = new Mock<IDialogServiceBase>(MockBehavior.Strict);
        var logger = new Mock<IWinoLogger>(MockBehavior.Strict);

        Assert.False(await UnlimitedAccountsStorePurchase.PurchaseAsync(store.Object, dialog.Object,
            logger.Object, new PlatformCapabilities()));

        store.VerifyNoOtherCalls();
        dialog.VerifyNoOtherCalls();
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AboutReview_WhenUnavailable_RejectsCommandAndDirectExecution()
    {
        var store = new Mock<IMicrosoftStoreService>(MockBehavior.Strict);
        var about = new AboutPageViewModel(store.Object, Mock.Of<IMailDialogService>(),
            Mock.Of<IExternalLauncher>(), Mock.Of<IClipboardService>(), Mock.Of<IAppMetadataService>(),
            Mock.Of<IPreferencesService>(), Mock.Of<IApplicationConfiguration>(), Mock.Of<IFileService>(),
            Mock.Of<IWinoLogger>(), new PlatformCapabilities());

        Assert.False(about.IsMicrosoftStoreAvailable);
        Assert.False(about.NavigateCommand.CanExecute("Store"));
        await about.NavigateCommand.ExecuteAsync("Store");

        store.VerifyNoOtherCalls();
    }
}
