using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class AccountSetupProgressPageViewController(AccountSetupProgressPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<AccountSetupProgressPageViewModel>(viewModel, dispatcher, logger)
{
    public override bool HasPendingWork => base.HasPendingWork || ViewModel.TryAgainCommand.IsRunning;

    public override void LoadView()
    {
        var back = CommandButton(Translator.AccountSetup_GoBackButton, ViewModel.GoBackCommand);
        var retry = CommandButton(Translator.AccountSetup_TryAgainButton, ViewModel.TryAgainCommand);
        var page = new AccountSetupProgressPage(back, retry);
        Bindings.Own(page.Steps.Bind(ViewModel.Steps, Dispatcher, ReportError));
        Bind(nameof(ViewModel.IsSetupComplete), vm => vm.IsSetupComplete, value => page.IsSetupComplete = value);
        Bind(nameof(ViewModel.IsSetupFailed), vm => vm.IsSetupFailed, value => page.IsSetupFailed = value);
        Bind(nameof(ViewModel.FailureMessage), vm => vm.FailureMessage, value => page.FailureMessage = value);
        View = page;
    }

    private void Bind<TValue>(string property, Func<AccountSetupProgressPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<AccountSetupProgressPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    protected override Task InitializeAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter!);
}
