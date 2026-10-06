using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class WelcomePageV2ViewController(WelcomePageV2ViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<WelcomePageV2ViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var status = NSTextField.CreateLabel(string.Empty);
        BindText(status, nameof(ViewModel.ImportStatusMessage), vm => vm.ImportStatusMessage);
        View = new WelcomePageV2(CommandButton(Translator.WelcomeWindow_GetStartedButton, ViewModel.GetStartedCommand),
            CommandButton(Translator.WelcomeWindow_ImportFromWinoAccount, ViewModel.ImportFromWinoAccountCommand),
            CommandButton(Translator.WelcomeWindow_ImportFromJsonFile, ViewModel.ImportFromJsonCommand), status);
    }
}
