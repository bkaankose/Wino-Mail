using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Store;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Mac App Store purchases follow the Microsoft Store flow: Unlimited Accounts works signed out and is
/// redeemed later; purchases made for the signed-in account are linked before StoreKit finishes them.
/// </summary>
public sealed class WinoAppStorePurchaseServiceTests : IAsyncLifetime
{
    private static readonly PlatformCapabilities AppStore = new(AppleAppStore: true);
    private InMemoryDatabaseService _database = null!;
    private readonly Mock<IAppStoreClient> _client = new();
    private readonly Mock<IWinoAccountApiClient> _api = new();
    private readonly Mock<IConfigurationService> _settings = new();

    public async Task InitializeAsync()
    {
        _database = new InMemoryDatabaseService();
        await _database.Connection.CreateTableAsync<WinoAccount>();
        _client.Setup(c => c.GetUnfinishedTransactionsAsync()).ReturnsAsync([]);
        _client.Setup(c => c.GetCurrentEntitlementsAsync()).ReturnsAsync([]);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Purchase_SignedOut_FinishesWithoutLinking()
    {
        var transaction = Transaction(1, AppStoreProductIds.UnlimitedAccounts, token: null);
        _client.Setup(c => c.PurchaseAsync(AppStoreProductIds.UnlimitedAccounts, null))
            .ReturnsAsync(new AppStorePurchase(AppStorePurchaseStatus.Purchased, transaction));

        var outcome = await CreateService().PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS);

        Assert.Equal(WinoAppStorePurchaseOutcome.Purchased, outcome);
        _client.Verify(c => c.FinishAsync(1), Times.Once);
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Purchase_SignedIn_TagsAccountAndFinishesAfterLink()
    {
        var account = await SignInAsync();
        var transaction = Transaction(2, AppStoreProductIds.UnlimitedAccounts, account.Id);
        _client.Setup(c => c.PurchaseAsync(AppStoreProductIds.UnlimitedAccounts, account.Id))
            .ReturnsAsync(new AppStorePurchase(AppStorePurchaseStatus.Purchased, transaction));
        SetupLink(AppStoreTransactionOutcomes.Linked, transaction);

        var outcome = await CreateService().PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS);

        Assert.Equal(WinoAppStorePurchaseOutcome.Purchased, outcome);
        _api.Verify(a => a.RedeemAppStoreTransactionsAsync(
            It.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == transaction.SignedTransaction), It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.FinishAsync(2), Times.Once);
    }

    [Fact]
    public async Task Purchase_LinkFails_LeavesTransactionUnfinished()
    {
        var account = await SignInAsync();
        var transaction = Transaction(3, AppStoreProductIds.AiPack, account.Id);
        _client.Setup(c => c.PurchaseAsync(AppStoreProductIds.AiPack, account.Id))
            .ReturnsAsync(new AppStorePurchase(AppStorePurchaseStatus.Purchased, transaction));
        _api.Setup(a => a.RedeemAppStoreTransactionsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<AppStoreRedeemResultDto>.Failure(ApiErrorCodes.AppStoreUnavailable));

        var outcome = await CreateService().PurchaseAsync(WinoAddOnProductType.AI_PACK);

        Assert.Equal(WinoAppStorePurchaseOutcome.PurchasedLinkPending, outcome);
        _client.Verify(c => c.FinishAsync(It.IsAny<ulong>()), Times.Never);
    }

    [Fact]
    public async Task AiPack_SignedOut_RequiresSignInWithoutOpeningTheStore()
    {
        var outcome = await CreateService().PurchaseAsync(WinoAddOnProductType.AI_PACK);

        Assert.Equal(WinoAppStorePurchaseOutcome.SignInRequired, outcome);
        _client.Verify(c => c.PurchaseAsync(It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task SyncOwned_LinksOnlyThisAccountsPurchasesAndFinishesUntaggedOnes()
    {
        var account = await SignInAsync();
        var mine = Transaction(10, AppStoreProductIds.AiPack, account.Id);
        var untagged = Transaction(11, AppStoreProductIds.UnlimitedAccounts, token: null);
        var someoneElse = Transaction(12, AppStoreProductIds.AiPack, Guid.NewGuid());
        _client.Setup(c => c.GetUnfinishedTransactionsAsync()).ReturnsAsync([untagged, someoneElse]);
        _client.Setup(c => c.GetCurrentEntitlementsAsync()).ReturnsAsync([mine, untagged, someoneElse]);
        SetupLink(AppStoreTransactionOutcomes.Linked, mine);

        Assert.True(await CreateService().SyncOwnedPurchasesAsync());

        _api.Verify(a => a.RedeemAppStoreTransactionsAsync(
            It.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == mine.SignedTransaction), It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.FinishAsync(10), Times.Once);
        _client.Verify(c => c.FinishAsync(11), Times.Once);
        _client.Verify(c => c.FinishAsync(12), Times.Never);
    }

    [Fact]
    public async Task Redeem_PurchaseOfAnotherAccount_ReportsAlreadyLinkedAndHidesTheCard()
    {
        await SignInAsync();
        var purchase = Transaction(20, AppStoreProductIds.UnlimitedAccounts, token: null);
        _client.Setup(c => c.GetCurrentEntitlementsAsync()).ReturnsAsync([purchase]);
        SetupLink(AppStoreTransactionOutcomes.LinkedToAnotherAccount, purchase);
        var service = CreateService();

        Assert.True(await service.HasRedeemCandidateAsync());
        Assert.Equal(WinoStorePurchaseRedeemOutcome.AlreadyLinked, await service.RedeemAsync());
        _settings.Verify(s => s.Set("AppStoreUnlimitedRedeemHidden_1020", true), Times.Once);
    }

    [Fact]
    public async Task Redeem_WhenAccountAlreadyUnlimited_HasNoCandidate()
    {
        await SignInAsync(unlimited: true);
        _client.Setup(c => c.GetCurrentEntitlementsAsync())
            .ReturnsAsync([Transaction(30, AppStoreProductIds.UnlimitedAccounts, token: null)]);

        Assert.False(await CreateService().HasRedeemCandidateAsync());
    }

    [Theory]
    [InlineData("USA", true)]
    [InlineData("POL", false)]
    public async Task ExternalPurchase_IsAllowedOnlyInTheUnitedStatesStorefront(string country, bool allowed)
    {
        _client.Setup(c => c.GetStorefrontCountryCodeAsync()).ReturnsAsync(country);

        Assert.Equal(allowed, await CreateService().IsExternalPurchaseAllowedAsync());
    }

    [Fact]
    public async Task Billing_CountsAnAppStorePurchaseAsUnlimitedAccounts()
    {
        _client.Setup(c => c.GetCurrentEntitlementsAsync())
            .ReturnsAsync([Transaction(40, AppStoreProductIds.UnlimitedAccounts, token: null)]);
        var billing = new WinoBillingService(_database, _api.Object, Mock.Of<IMicrosoftStoreService>(), Mock.Of<IExternalLauncher>(),
            AppStore, appStorePurchases: CreateService());

        Assert.True(await billing.HasUnlimitedAccountsAsync());
    }

    [Fact]
    public async Task WithoutTheAppStore_DoesNothing()
    {
        var client = new Mock<IAppStoreClient>(MockBehavior.Strict);
        var service = new WinoAppStorePurchaseService(_database, _api.Object, _settings.Object, new PlatformCapabilities(), client.Object);

        Assert.False(service.IsAvailable);
        Assert.False(await service.HasUnlimitedAccountsAsync());
        Assert.Equal(WinoAppStorePurchaseOutcome.Failed, await service.PurchaseAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS));
        Assert.False(await service.SyncOwnedPurchasesAsync());
        Assert.Equal(WinoStorePurchaseRedeemOutcome.Unavailable, await service.RedeemAsync());
        client.VerifyNoOtherCalls();
        _api.VerifyNoOtherCalls();
    }

    private WinoAppStorePurchaseService CreateService()
        => new(_database, _api.Object, _settings.Object, AppStore, _client.Object);

    private async Task<WinoAccount> SignInAsync(bool unlimited = false)
    {
        var account = new WinoAccount { Id = Guid.NewGuid(), Email = "user@example.com", IsUnlimitedAccountsEnabled = unlimited };
        await _database.Connection.InsertAsync(account);
        return account;
    }

    private void SetupLink(string outcome, AppStoreTransactionInfo transaction)
        => _api.Setup(a => a.RedeemAppStoreTransactionsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<AppStoreRedeemResultDto>.Success(new AppStoreRedeemResultDto(
                outcome == AppStoreTransactionOutcomes.Linked, false,
                [new AppStoreTransactionResultDto(transaction.OriginalId.ToString(), transaction.ProductId, transaction.ProductId, outcome)])));

    /// <summary>The original ID is the transaction ID plus 1000, so tests can tell them apart.</summary>
    private static AppStoreTransactionInfo Transaction(ulong id, string productId, Guid? token)
        => new(id, id + 1000, productId, token, $"jws-{id}", IsRevoked: false);
}
