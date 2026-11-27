using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;
using AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class WelcomePageV2ViewController(WelcomePageV2ViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<WelcomePageV2ViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var status = NSTextField.CreateLabel(string.Empty);
        BindText(status, nameof(ViewModel.ImportStatusMessage), vm => vm.ImportStatusMessage);
        var restoreLinks = new[]
        {
            RestoreLink(Translator.WelcomeWindow_RestoreLinkWinoAccount, Translator.WelcomeWindow_ImportFromWinoAccount, ViewModel.ImportFromWinoAccountCommand),
            RestoreLink(Translator.WelcomeWindow_BackupFileLink, Translator.WelcomeWindow_ImportFromJsonFile, ViewModel.ImportFromJsonCommand),
            RestoreLink(Translator.WelcomeWindow_RestoreLinkOtherClients, Translator.MailClientImport_Title, ViewModel.ImportFromOtherMailClientsCommand)
        };
        View = new WelcomePageV2(CommandButton(Translator.WelcomeWindow_GetStartedButton, ViewModel.GetStartedCommand), restoreLinks, status);
    }

    private NSButton RestoreLink(string text, string accessibleName, System.Windows.Input.ICommand command)
    {
        var button = CommandButton(text, command);
        WinoAccessibility.Label(button, accessibleName);
        return button;
    }
}
