using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views;

public sealed class AccountSetupProgressPageViewController(AccountSetupProgressPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<AccountSetupProgressPageViewModel>(viewModel, dispatcher, logger)
{
    public override bool HasPendingWork => base.HasPendingWork || ViewModel.TryAgainCommand.IsRunning;

    public override void LoadView()
    {
        var back = CommandButton(Translator.AccountSetup_GoBackButton, ViewModel.GoBackCommand);
        var retry = CommandButton(Translator.AccountSetup_TryAgainButton, ViewModel.TryAgainCommand, primary: true);
        var page = new AccountSetupProgressPage(back, retry);
        Bindings.Own(page.Steps.Bind(ViewModel.Steps, Dispatcher, ReportError));
        Bind(nameof(ViewModel.IsSetupComplete), vm => vm.IsSetupComplete, value => page.IsSetupComplete = value);
        Bind(nameof(ViewModel.IsSetupFailed), vm => vm.IsSetupFailed, value => page.IsSetupFailed = value);
        Bind(nameof(ViewModel.FailureMessage), vm => vm.FailureMessage, value => page.FailureMessage = value);
        View = page;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);
}
