using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Store;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoStorePurchaseRedeemServiceTests : IAsyncLifetime
{
    private const string StoreUserId = "store-user-0001";

    private static readonly string StoreIdKey = CreateStoreIdKey(
        $$"""{"http://schemas.microsoft.com/marketplace/2015/08/claims/key/userId":"{{StoreUserId}}","http://schemas.microsoft.com/marketplace/2015/08/claims/key/clientId":"client"}""");

    private readonly Mock<IWinoAccountApiClient> _apiClient = new();
    private readonly Mock<IMicrosoftStoreService> _storeService = new();
    private readonly InMemoryConfiguration _configuration = new();
    private InMemoryDatabaseService _databaseService = null!;
    private WinoStorePurchaseRedeemService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new WinoStorePurchaseRedeemService(_databaseService, _apiClient.Object, _storeService.Object, _configuration);

        _storeService.Setup(x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)).ReturnsAsync(true);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync("service-ticket", "publisher-user")).ReturnsAsync(StoreIdKey);
        _apiClient
            .Setup(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<StoreCollectionsIdTicketResultDto>.Success(
                new StoreCollectionsIdTicketResultDto("service-ticket", "publisher-user", DateTimeOffset.UtcNow.AddHours(1))));
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task Candidate_SignedOut_DoesNotRequestTicket()
    {
        (await _service.GetRedeemCandidateAsync()).Should().BeNull();

        _storeService.Verify(x => x.HasProductAsync(It.IsAny<WinoAddOnProductType>()), Times.Never);
        _apiClient.Verify(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Candidate_AlreadyUnlockedOrNoStoreLicense_DoesNotRequestTicket(bool accountHasAddOn, bool storeHasLicense)
    {
        await InsertAccountAsync(accountHasAddOn);
        _storeService.Setup(x => x.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS)).ReturnsAsync(storeHasLicense);

        (await _service.GetRedeemCandidateAsync()).Should().BeNull();

        _apiClient.Verify(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()), Times.Never);
        _storeService.Verify(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Candidate_StoreLicenseWithoutAccountAddOn_HashesStoreUserId()
    {
        await InsertAccountAsync(false);

        var candidate = await _service.GetRedeemCandidateAsync();

        candidate.Should().NotBeNull();
        candidate!.StoreIdKey.Should().Be(StoreIdKey);
        candidate.StoreUserHash.Should().Be(Sha256Hex(StoreUserId));
        _apiClient.Verify(x => x.RedeemStoreUnlimitedAccountsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Candidate_KeyWithoutUserIdClaim_UsesUnknownHash()
    {
        await InsertAccountAsync(false);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(CreateStoreIdKey("""{"other":"value"}"""));

        var candidate = await _service.GetRedeemCandidateAsync();

        candidate!.StoreUserHash.Should().Be(WinoStorePurchaseRedeemService.UnknownStoreUserHash);
    }

    [Fact]
    public async Task Candidate_TicketFailure_ReturnsNull()
    {
        await InsertAccountAsync(false);
        _apiClient
            .Setup(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<StoreCollectionsIdTicketResultDto>.Failure(ApiErrorCodes.MicrosoftStoreUnavailable));

        (await _service.GetRedeemCandidateAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Candidate_StoreReturnsNoKey_ReturnsNull()
    {
        await InsertAccountAsync(false);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);

        (await _service.GetRedeemCandidateAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Candidate_StoreThrows_ReturnsNull()
    {
        await InsertAccountAsync(false);
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        (await _service.GetRedeemCandidateAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseAlreadyLinked)]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseNotFound)]
    public async Task Redeem_PermanentRejection_HidesCandidateForThatStoreUserOnly(string errorCode)
    {
        await InsertAccountAsync(false);
        SetupRedeem(ApiEnvelope<StorePurchaseRedeemResultDto>.Failure(errorCode));
        var candidate = await _service.GetRedeemCandidateAsync();

        await _service.RedeemUnlimitedAccountsAsync(candidate!);

        (await _service.GetRedeemCandidateAsync()).Should().BeNull();

        // A different Store user on this device gets the card again.
        _storeService.Setup(x => x.GetCustomerCollectionsIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(CreateStoreIdKey("""{"http://schemas.microsoft.com/marketplace/2015/08/claims/key/userId":"another-store-user"}"""));
        (await _service.GetRedeemCandidateAsync())!.StoreUserHash.Should().Be(Sha256Hex("another-store-user"));
    }

    [Fact]
    public async Task Redeem_Failure_KeepsCandidate()
    {
        await InsertAccountAsync(false);
        SetupRedeem(ApiEnvelope<StorePurchaseRedeemResultDto>.Failure(ApiErrorCodes.MicrosoftStoreUnavailable));
        var candidate = await _service.GetRedeemCandidateAsync();

        await _service.RedeemUnlimitedAccountsAsync(candidate!);

        (await _service.GetRedeemCandidateAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_Success_SendsCandidateStoreIdKey()
    {
        SetupRedeem(ApiEnvelope<StorePurchaseRedeemResultDto>.Success(new StorePurchaseRedeemResultDto(true)));

        var outcome = await _service.RedeemUnlimitedAccountsAsync(new WinoStoreRedeemCandidate("hash", StoreIdKey));

        outcome.Should().Be(WinoStorePurchaseRedeemOutcome.Redeemed);
        _apiClient.Verify(x => x.RedeemStoreUnlimitedAccountsAsync(StoreIdKey, It.IsAny<CancellationToken>()), Times.Once);
        _apiClient.Verify(x => x.CreateStoreCollectionsIdTicketAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseAlreadyLinked, WinoStorePurchaseRedeemOutcome.AlreadyLinked)]
    [InlineData(ApiErrorCodes.MicrosoftStorePurchaseNotFound, WinoStorePurchaseRedeemOutcome.NotOwned)]
    [InlineData(ApiErrorCodes.MicrosoftStoreUnavailable, WinoStorePurchaseRedeemOutcome.Failed)]
    public async Task Redeem_ServerRejection_MapsToOutcome(string errorCode, WinoStorePurchaseRedeemOutcome expected)
    {
        SetupRedeem(ApiEnvelope<StorePurchaseRedeemResultDto>.Failure(errorCode));

        (await _service.RedeemUnlimitedAccountsAsync(new WinoStoreRedeemCandidate("hash", StoreIdKey))).Should().Be(expected);
    }

    [Fact]
    public async Task Redeem_ApiThrows_ReportsFailure()
    {
        _apiClient
            .Setup(x => x.RedeemStoreUnlimitedAccountsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API unavailable"));

        (await _service.RedeemUnlimitedAccountsAsync(new WinoStoreRedeemCandidate("hash", StoreIdKey)))
            .Should().Be(WinoStorePurchaseRedeemOutcome.Failed);
    }

    private void SetupRedeem(ApiEnvelope<StorePurchaseRedeemResultDto> result)
        => _apiClient
            .Setup(x => x.RedeemStoreUnlimitedAccountsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private Task InsertAccountAsync(bool hasUnlimitedAccounts)
        => _databaseService.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
            Email = "store@example.com",
            IsUnlimitedAccountsEnabled = hasUnlimitedAccounts
        });

    private static string CreateStoreIdKey(string payloadJson)
        => $"{Base64Url("""{"alg":"RS256","typ":"JWT"}""")}.{Base64Url(payloadJson)}.signature";

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class InMemoryConfiguration : IConfigurationService
    {
        private readonly Dictionary<string, object> _values = new();

        public bool Contains(string key) => _values.ContainsKey(key);
        public bool Remove(string key) => _values.Remove(key);
        public void Set(string key, object value) => _values[key] = value;
        public T Get<T>(string key, T defaultValue = default!) => _values.TryGetValue(key, out var value) ? (T)value : defaultValue;
        public void SetRoaming(string key, object value) => Set(key, value);
        public T GetRoaming<T>(string key, T defaultValue = default!) => Get(key, defaultValue);
    }
}
