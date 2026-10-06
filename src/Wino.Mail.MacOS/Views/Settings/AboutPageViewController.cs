using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Settings;

public sealed class AboutPageViewController(AboutPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, INavigationService navigation)
    : WinoViewController<AboutPageViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var back = new NSButton { Title = Translator.Buttons_Back };
        EventHandler clicked = (_, _) => navigation.GoBack();
        back.Activated += clicked;
        Bindings.Own(new ActionDisposable(() => back.Activated -= clicked));
        View = new AboutPage(back,
        NSTextField.CreateLabel(Translator.SettingsAbout_Title), NSTextField.CreateLabel(ViewModel.VersionName),
        CommandButton(Translator.SettingsWebsite_Title, ViewModel.NavigateCommand, () => ViewModel.WebsiteUrl),
        CommandButton("GitHub", ViewModel.NavigateCommand, () => ViewModel.GitHubUrl),
        CommandButton(Translator.SettingsPrivacyPolicy_Title, ViewModel.NavigateCommand, () => ViewModel.PrivacyPolicyUrl),
        CommandButton(Translator.Buttons_Copy, ViewModel.CopyDiagnosticIdCommand));
    }
}
