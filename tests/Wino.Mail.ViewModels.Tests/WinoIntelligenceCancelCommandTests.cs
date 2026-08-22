using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// Cancel is the one action a running job must never lose.
/// </summary>
/// <remarks>
/// It used to require <c>!IsBusy</c>, and <c>IsBusy</c> did not notify this command at all — so any
/// page load, download or delete that set and cleared the busy flag left the button's cached
/// CanExecute stuck at false for the rest of the job, with no way to stop it.
/// </remarks>
public sealed class WinoIntelligenceCancelCommandTests
{
    [Fact]
    public void Cancel_IsEnabledWheneverAJobIsActive()
    {
        var viewModel = CreateViewModel();

        viewModel.CancelIndexingCommand.CanExecute(null).Should().BeFalse();

        viewModel.IsJobActive = true;
        viewModel.CancelIndexingCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Cancel_StaysEnabledWhileThePageIsBusy()
    {
        var viewModel = CreateViewModel();
        viewModel.IsJobActive = true;

        // A page load sets and clears the busy flag around its work.
        viewModel.IsBusy = true;
        viewModel.CancelIndexingCommand.CanExecute(null).Should().BeTrue();

        viewModel.IsBusy = false;
        viewModel.CancelIndexingCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Cancel_RaisesCanExecuteChangedWhenTheBusyFlagMoves()
    {
        var viewModel = CreateViewModel();
        viewModel.IsJobActive = true;

        var raised = 0;
        viewModel.CancelIndexingCommand.CanExecuteChanged += (_, _) => raised++;

        viewModel.IsBusy = true;
        viewModel.IsBusy = false;

        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Cancel_IsDisabledOnceTheJobEnds()
    {
        var viewModel = CreateViewModel();
        viewModel.IsJobActive = true;
        viewModel.IsJobActive = false;

        viewModel.CancelIndexingCommand.CanExecute(null).Should().BeFalse();
    }

    private static WinoIntelligenceManagementPageViewModel CreateViewModel()
        => new(
            Mock.Of<IMailDialogService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IFolderService>(),
            Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(),
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<ILocalIntelligenceStore>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IIntelligenceCoverageHandoff>());
}
