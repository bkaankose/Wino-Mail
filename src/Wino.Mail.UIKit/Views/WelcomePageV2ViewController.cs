using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views;

public sealed class WelcomePageV2ViewController(WelcomePageV2ViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<WelcomePageV2ViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var status = new UILabel();
        BindText(status, nameof(ViewModel.ImportStatusMessage), vm => vm.ImportStatusMessage);
        var getStarted = CommandButton(Translator.WelcomeWindow_GetStartedButton, ViewModel.GetStartedCommand, primary: true);
        var restoreLinks = new[]
        {
            RestoreLink(Translator.WelcomeWindow_RestoreLinkWinoAccount, Translator.WelcomeWindow_ImportFromWinoAccount, ViewModel.ImportFromWinoAccountCommand),
            RestoreLink(Translator.WelcomeWindow_BackupFileLink, Translator.WelcomeWindow_ImportFromJsonFile, ViewModel.ImportFromJsonCommand)
        };
        View = new WelcomePageV2(getStarted, restoreLinks, status);
    }

    /// <summary>The restore actions read as links under the primary button, with the full name for VoiceOver.</summary>
    private UIButton RestoreLink(string text, string accessibleName, System.Windows.Input.ICommand command)
    {
        var button = CommandButton(text, command);
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = button.Configuration?.Title ?? string.Empty;
        button.Configuration = configuration;
        button.AccessibilityLabel = accessibleName;
        return button;
    }
}
