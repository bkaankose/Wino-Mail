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
        var importAccount = Link(CommandButton(Translator.WelcomeWindow_ImportFromWinoAccount, ViewModel.ImportFromWinoAccountCommand));
        var importFile = Link(CommandButton(Translator.WelcomeWindow_ImportFromJsonFile, ViewModel.ImportFromJsonCommand));
        View = new WelcomePageV2(getStarted, importAccount, importFile, status);
    }

    /// <summary>The restore actions read as links under the primary button.</summary>
    private static UIButton Link(UIButton button)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = button.Configuration?.Title ?? string.Empty;
        button.Configuration = configuration;
        return button;
    }
}
