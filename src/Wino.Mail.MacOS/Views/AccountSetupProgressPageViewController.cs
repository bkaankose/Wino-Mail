using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit;
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
        var steps = Bindings.Own(new WinoCollectionView<AccountSetupStepModel>(step => step.Title, step => step.AutomationName, Dispatcher, ReportError));
        steps.Bind(ViewModel.Steps);
        steps.HeightAnchor.ConstraintEqualTo(280).Active = true;
        var error = NSTextField.CreateLabel(string.Empty);
        BindText(error, nameof(ViewModel.FailureMessage), vm => vm.FailureMessage);
        var retry = CommandButton(Translator.Buttons_Retry, ViewModel.TryAgainCommand);
        var back = CommandButton(Translator.Buttons_Back, ViewModel.GoBackCommand);
        Bindings.Own(new PropertyBinding<AccountSetupProgressPageViewModel, bool>(ViewModel, nameof(ViewModel.IsSetupFailed),
            vm => vm.IsSetupFailed, failed => { retry.Hidden = !failed; back.Hidden = !failed; }, Dispatcher, ReportError));
        View = new AccountSetupProgressPage(NSTextField.CreateLabel(Translator.WelcomeWizard_Step3Title), steps, error, retry, back);
    }
    protected override Task InitializeAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter!);
}
