using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Store;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoStorePurchaseRedeemServiceTests : IAsyncLifetime
{
    private const string StoreIdKey = "store-id-key";

    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private readonly Mock<IMicrosoftStoreService> _storeService = new();
    private InMemoryDatabaseService _databaseService = null!;
    private WinoStorePurchaseRedeemService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new WinoStorePurchaseRedeemService(_databaseService, _apiClient.Object, _storeService.Object);

        _storeService.Setup(x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)).ReturnsAsync(true);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync("service-ticket", "publisher-user")).ReturnsAsync(StoreIdKey);
        _apiClient
            .Setup(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<StoreCollectionsIdTicketResultDto>.Success(
                new StoreCollectionsIdTicketResultDto("service-ticket", "publisher-user", DateTimeOffset.UtcNow.AddHours(1))));
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task Redeem_SignedOut_DoesNothing()
    {
        var outcome = await _service.RedeemUnlimitedAccountsAsync();

        outcome.Should().Be(WinoStorePurchaseRedeemOutcome.NotNeeded);
        _storeService.Verify(x => x.HasProductAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Redeem_AlreadyUnlockedOrNoStoreLicense_DoesNotCallTheApi(bool accountHasAddOn, bool storeHasLicense)
    {
        await InsertAccountAsync(accountHasAddOn);
        _storeService.Setup(x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)).ReturnsAsync(storeHasLicense);

        var outcome = await _service.RedeemUnlimitedAccountsAsync();

        outcome.Should().Be(WinoStorePurchaseRedeemOutcome.NotNeeded);
        _apiClient.Verify(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_StoreLicenseWithoutAccountAddOn_SendsStoreIdKey()
    {
        await InsertAccountAsync(false);
        _apiClient
            .Setup(x => x.RedeemStoreUnlimitedAccountsAsync(StoreIdKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<StorePurchaseRedeemResultDto>.Success(new StorePurchaseRedeemResultDto(true)));

        var outcome = await _service.RedeemUnlimitedAccountsAsync();

        outcome.Should().Be(WinoStorePurchaseRedeemOutcome.Redeemed);
        _apiClient.Verify(x => x.RedeemStoreUnlimitedAccountsAsync(StoreIdKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseAlreadyLinked, WinoStorePurchaseRedeemOutcome.AlreadyLinked)]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseNotFound, WinoStorePurchaseRedeemOutcome.NotOwned)]
    [InlineData(ApiErrorCodes.MicrosoftStoreUnavailable, WinoStorePurchaseRedeemOutcome.Failed)]
    public async Task Redeem_ServerRejection_MapsToOutcome(string errorCode, WinoStorePurchaseRedeemOutcome expected)
    {
        await InsertAccountAsync(false);
        _apiClient
            .Setup(x => x.RedeemStoreUnlimitedAccountsAsync(StoreIdKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<StorePurchaseRedeemResultDto>.Failure(errorCode));

        (await _service.RedeemUnlimitedAccountsAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task Redeem_StoreReturnsNoKey_FailsWithoutRedeemCall()
    {
        await InsertAccountAsync(false);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

        (await _service.RedeemUnlimitedAccountsAsync()).Should().Be(WinoStorePurchaseRedeemOutcome.Failed);
        _apiClient.Verify(x => x.RedeemStoreUnlimitedAccountsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_StoreThrows_ReportsFailure()
    {
        await InsertAccountAsync(false);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        (await _service.RedeemUnlimitedAccountsAsync()).Should().Be(WinoStorePurchaseRedeemOutcome.Failed);
    }

    private Task InsertAccountAsync(bool hasUnlimitedAccounts)
        => _databaseService.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "store@example.com",
            IsUnlimitedAccountsEnabled = hasUnlimitedAccounts
        });
}
