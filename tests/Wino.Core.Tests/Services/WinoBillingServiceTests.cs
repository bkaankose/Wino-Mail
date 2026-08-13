using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoBillingServiceTests : IAsyncLifetime
{
    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private readonly Mock<IStoreManagementService> _storeManagementService = new();
    private readonly Mock<INativeAppService> _nativeAppService = new();
    private InMemoryDatabaseService _databaseService = null!;
    private WinoBillingService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new WinoBillingService(_databaseService, _apiClient.Object, _storeManagementService.Object, _nativeAppService.Object);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Theory]
    [InlineData(WinoAddOnProductType.AI_PACK, "AI_PACK")]
    [InlineData(WinoAddOnProductType.UNLIMITED_ACCOUNTS, "UNLIMITED_ACCOUNTS")]
    public async Task CreateCheckoutSessionAsync_UsesExpectedProductCode(WinoAddOnProductType productType, string productCode)
    {
        var expected = ApiEnvelope<CheckoutSessionResultDto>.Success(
            new CheckoutSessionResultDto("https://checkout.stripe.com/test", DateTimeOffset.UtcNow.AddMinutes(30)));
        _apiClient.Setup(x => x.CreateCheckoutSessionAsync(productCode, default)).ReturnsAsync(expected);

        var result = await _service.CreateCheckoutSessionAsync(productType);

        result.Should().BeSameAs(expected);
        _apiClient.Verify(x => x.CreateCheckoutSessionAsync(productCode, default), Times.Once);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task HasUnlimitedAccountsAsync_CombinesApiAndLegacyStoreOwnership(
        bool apiEntitlement,
        bool storeEntitlement,
        bool expected)
    {
        await _databaseService.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "billing@example.com",
            IsUnlimitedAccountsEnabled = apiEntitlement
        });
        _storeManagementService
            .Setup(x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS))
            .ReturnsAsync(storeEntitlement);

        var result = await _service.HasUnlimitedAccountsAsync();

        result.Should().Be(expected);
        _storeManagementService.Verify(
            x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS),
            apiEntitlement ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsApiBillingStatus()
    {
        var status = new BillingStatusResultDto(
            true,
            new AiPackBillingStatusDto("Active", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1), DateTimeOffset.UtcNow.AddMonths(1), false));
        var expected = ApiEnvelope<BillingStatusResultDto>.Success(status);
        _apiClient.Setup(x => x.GetBillingStatusAsync(default)).ReturnsAsync(expected);

        var result = await _service.GetStatusAsync();

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task OpenCheckoutAsync_ReturnsFalse_WhenBrowserLaunchFails()
    {
        var checkoutUri = new Uri("https://checkout.stripe.com/test");
        _apiClient
            .Setup(x => x.CreateCheckoutSessionAsync("AI_PACK", default))
            .ReturnsAsync(ApiEnvelope<CheckoutSessionResultDto>.Success(
                new CheckoutSessionResultDto(checkoutUri.AbsoluteUri, DateTimeOffset.UtcNow.AddMinutes(30))));
        _nativeAppService.Setup(x => x.LaunchUriAsync(checkoutUri)).ReturnsAsync(false);

        var result = await _service.OpenCheckoutAsync(WinoAddOnProductType.AI_PACK);

        result.Should().BeFalse();
        _nativeAppService.Verify(x => x.LaunchUriAsync(checkoutUri), Times.Once);
    }

    [Fact]
    public async Task OpenCheckoutAsync_RejectsNonHttpsCheckoutUrl()
    {
        _apiClient
            .Setup(x => x.CreateCheckoutSessionAsync("AI_PACK", default))
            .ReturnsAsync(ApiEnvelope<CheckoutSessionResultDto>.Success(
                new CheckoutSessionResultDto("http://checkout.example.test", DateTimeOffset.UtcNow.AddMinutes(30))));

        var result = await _service.OpenCheckoutAsync(WinoAddOnProductType.AI_PACK);

        result.Should().BeFalse();
        _nativeAppService.Verify(x => x.LaunchUriAsync(It.IsAny<Uri>()), Times.Never());
    }

    [Fact]
    public async Task OpenCheckoutAsync_ReturnsFalse_WhenCheckoutSessionFails()
    {
        _apiClient
            .Setup(x => x.CreateCheckoutSessionAsync("AI_PACK", default))
            .ReturnsAsync(ApiEnvelope<CheckoutSessionResultDto>.Failure("CHECKOUT_FAILED"));

        var result = await _service.OpenCheckoutAsync(WinoAddOnProductType.AI_PACK);

        result.Should().BeFalse();
        _nativeAppService.Verify(x => x.LaunchUriAsync(It.IsAny<Uri>()), Times.Never());
    }
}
