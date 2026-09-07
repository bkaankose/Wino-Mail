using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class WinoIntelligenceIndicatorSettingsTests
{
    [Fact]
    public async Task CachedPageReload_ClosesInteractionGateBeforeLoadingAccount()
    {
        var accountId = Guid.NewGuid();
        var accountLoad = new TaskCompletionSource<MailAccount>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountService = new Mock<IAccountService>();
        accountService
            .Setup(service => service.GetAccountAsync(accountId))
            .Returns(accountLoad.Task);
        var viewModel = CreateViewModel(accountService.Object);
        viewModel.IsPageReady = true;

        viewModel.OnNavigatedTo(NavigationMode.New, accountId);

        viewModel.IsPageReady.Should().BeFalse();
        viewModel.CanChangeIntelligencePreferences.Should().BeFalse();

        accountLoad.SetException(new InvalidOperationException("Stop after the readiness assertion"));
        await WaitUntilAsync(() => viewModel.IsPageReady && !viewModel.IsBusy);
    }

    [Fact]
    public async Task VisibilityChanges_AreSerializedAndPreserveEarlierChanges()
    {
        var firstUpdateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCount = 0;
        var accountService = new Mock<IAccountService>();
        accountService
            .Setup(service => service.UpdateAccountPreferencesAsync(It.IsAny<MailAccountPreferences>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref updateCount) == 1)
                {
                    firstUpdateEntered.SetResult();
                    await releaseFirstUpdate.Task;
                }
            });
        var accountId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = accountId,
            Preferences = new MailAccountPreferences
            {
                Id = Guid.NewGuid(),
                AccountId = accountId
            }
        };
        var viewModel = CreateViewModel(accountService.Object);
        viewModel.Account = account;
        viewModel.IsPageReady = true;

        var hideDeadline = viewModel.SetIntelligenceIndicatorVisibilityAsync(
            IntelligenceIndicatorId.FactDeadline,
            false);
        await firstUpdateEntered.Task;

        var hidePriority = viewModel.SetIntelligenceIndicatorVisibilityAsync(
            IntelligenceIndicatorId.FactPriority,
            false);
        updateCount.Should().Be(1);

        releaseFirstUpdate.SetResult();
        await Task.WhenAll(hideDeadline, hidePriority);

        updateCount.Should().Be(2);
        account.Preferences.ExcludedIntelligenceIndicatorIds.Should().BeEquivalentTo(
            IntelligenceIndicatorId.FactDeadline,
            IntelligenceIndicatorId.FactPriority);
    }

    [Fact]
    public async Task VisibilityChangeFailure_RestoresPreferenceAndToggleState()
    {
        var accountService = new Mock<IAccountService>();
        accountService
            .Setup(service => service.UpdateAccountPreferencesAsync(It.IsAny<MailAccountPreferences>()))
            .ThrowsAsync(new InvalidOperationException("Save failed"));
        var account = CreateAccount();
        var item = CreateIndicator(isVisible: false);
        var viewModel = CreateViewModel(accountService.Object);
        viewModel.Account = account;
        viewModel.IntelligenceIndicatorSettings.Add(item);
        viewModel.IsPageReady = true;

        var actualState = await viewModel.SetIntelligenceIndicatorVisibilityAsync(
            IntelligenceIndicatorId.FactDeadline,
            false);

        actualState.Should().BeTrue();
        account.Preferences.ExcludedIntelligenceIndicatorIds.Should().BeEmpty();
        item.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task VisibilityChangeCompletion_DoesNotMutateNewAccountAfterNavigation()
    {
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountService = new Mock<IAccountService>();
        accountService
            .Setup(service => service.UpdateAccountPreferencesAsync(It.IsAny<MailAccountPreferences>()))
            .Returns(async () =>
            {
                updateEntered.SetResult();
                await releaseUpdate.Task;
            });
        var originalAccount = CreateAccount();
        var nextAccount = CreateAccount();
        var nextAccountItem = CreateIndicator(isVisible: true);
        var viewModel = CreateViewModel(accountService.Object);
        viewModel.Account = originalAccount;
        viewModel.IntelligenceIndicatorSettings.Add(CreateIndicator(isVisible: false));
        viewModel.IsPageReady = true;

        var save = viewModel.SetIntelligenceIndicatorVisibilityAsync(
            IntelligenceIndicatorId.FactDeadline,
            false);
        await updateEntered.Task;

        viewModel.Account = nextAccount;
        viewModel.IntelligenceIndicatorSettings.Clear();
        viewModel.IntelligenceIndicatorSettings.Add(nextAccountItem);
        releaseUpdate.SetResult();
        await save;

        originalAccount.Preferences.ExcludedIntelligenceIndicatorIds.Should()
            .ContainSingle(IntelligenceIndicatorId.FactDeadline);
        nextAccount.Preferences.ExcludedIntelligenceIndicatorIds.Should().BeEmpty();
        nextAccountItem.IsVisible.Should().BeTrue();
    }

    private static MailAccount CreateAccount()
    {
        var accountId = Guid.NewGuid();
        return new MailAccount
        {
            Id = accountId,
            Preferences = new MailAccountPreferences
            {
                Id = Guid.NewGuid(),
                AccountId = accountId
            }
        };
    }

    private static IntelligenceIndicatorSettingsItem CreateIndicator(bool isVisible)
        => new(
            IntelligenceIndicatorId.FactDeadline,
            "Deadline",
            "DeadlineToggle",
            string.Empty,
            isVisible);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
            await Task.Delay(10);

        condition().Should().BeTrue();
    }

    private static WinoIntelligenceManagementPageViewModel CreateViewModel(IAccountService accountService)
        => new(
            Mock.Of<IMailDialogService>(),
            accountService,
            Mock.Of<IFolderService>(),
            Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(),
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<ILocalIntelligenceStore>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IIntelligenceCoverageHandoff>());
}
