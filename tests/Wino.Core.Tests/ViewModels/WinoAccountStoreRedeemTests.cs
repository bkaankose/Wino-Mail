using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Xunit;

namespace Wino.Core.Tests.ViewModels;

public sealed class WinoAccountStoreRedeemTests
{
    private static readonly WinoStoreRedeemCandidate Candidate = new("hash", "store-id-key");

    private readonly WinoAccount _account = new()
    {
        Id = Guid.NewGuid(),
        Email = "store@example.test",
        AccessToken = "access-token",
        AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private readonly Mock<IWinoAccountProfileService> _profile = new();
    private readonly Mock<IWinoStorePurchaseRedeemService> _redeem = new();
    private readonly Mock<IMailDialogService> _dialogs = new();

    public WinoAccountStoreRedeemTests()
    {
        _profile.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(_account);
        _profile.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_account);
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Success(_account));
    }

    [Fact]
    public async Task Load_WithoutCandidate_HidesCard()
    {
        var checkedCandidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _redeem.Setup(x => x.GetRedeemCandidateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => checkedCandidate.TrySetResult())
            .ReturnsAsync((WinoStoreRedeemCandidate?)null);
        var viewModel = CreateViewModel();

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        await checkedCandidate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.IsSignedIn && !viewModel.IsBusy);
        viewModel.ShowStoreRedeemCard.Should().BeFalse();
    }

    [Fact]
    public async Task Redeem_Failed_KeepsCardAndShowsError()
    {
        _redeem.Setup(x => x.GetRedeemCandidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Candidate);
        _redeem.Setup(x => x.RedeemUnlimitedAccountsAsync(Candidate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoStorePurchaseRedeemOutcome.Failed);
        var viewModel = await LoadWithCardAsync();

        await viewModel.RedeemStorePurchaseCommand.ExecuteAsync(null);

        viewModel.ShowStoreRedeemCard.Should().BeTrue();
        viewModel.IsStoreRedeemInProgress.Should().BeFalse();
        _dialogs.Verify(x => x.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Error), Times.Once);
    }

    [Theory]
    [InlineData(WinoStorePurchaseRedeemOutcome.AlreadyLinked)]
    [InlineData(WinoStorePurchaseRedeemOutcome.NotOwned)]
    public async Task Redeem_PermanentRejection_HidesCardWithoutReload(WinoStorePurchaseRedeemOutcome outcome)
    {
        _redeem.Setup(x => x.GetRedeemCandidateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Candidate);
        _redeem.Setup(x => x.RedeemUnlimitedAccountsAsync(Candidate, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);
        var viewModel = await LoadWithCardAsync();

        await viewModel.RedeemStorePurchaseCommand.ExecuteAsync(null);

        viewModel.ShowStoreRedeemCard.Should().BeFalse();
        _profile.Verify(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Redeem_Redeemed_RefreshesProfileAndHidesCard()
    {
        _redeem.SetupSequence(x => x.GetRedeemCandidateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate)
            .ReturnsAsync((WinoStoreRedeemCandidate?)null);
        _redeem.Setup(x => x.RedeemUnlimitedAccountsAsync(Candidate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoStorePurchaseRedeemOutcome.Redeemed);
        var viewModel = await LoadWithCardAsync();

        await viewModel.RedeemStorePurchaseCommand.ExecuteAsync(null);

        viewModel.ShowStoreRedeemCard.Should().BeFalse();
        _profile.Verify(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()), Times.Once);
        _dialogs.Verify(x => x.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Success), Times.Once);
    }

    private async Task<WinoAccountManagementPageViewModel> LoadWithCardAsync()
    {
        var viewModel = CreateViewModel();
        viewModel.OnNavigatedTo(NavigationMode.New, null!);
        await WaitUntilAsync(() => viewModel.ShowStoreRedeemCard && !viewModel.IsBusy);
        return viewModel;
    }

    private WinoAccountManagementPageViewModel CreateViewModel()
    {
        var billing = new Mock<IWinoBillingService>();
        billing.Setup(x => x.HasUnlimitedAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<BillingStatusResultDto>.Failure("NOT_READY"));

        return new WinoAccountManagementPageViewModel(
            profileService: _profile.Object,
            dialogService: _dialogs.Object,
            billingService: billing.Object,
            apiClient: Mock.Of<IWinoAccountApiClient>(),
            accountService: Mock.Of<IAccountService>(),
            semanticIndexCoordinator: Mock.Of<IMailIntelligenceCoordinator>(),
            preferencesService: Mock.Of<IPreferencesService>(),
            storeRedeemService: _redeem.Object);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }
}
