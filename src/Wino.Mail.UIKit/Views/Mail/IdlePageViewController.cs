using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Reading pane idle page (Mac IdlePageViewController, Windows IdlePage): the Wino mail illustration with "No message
/// selected" and the keyboard hints, or the mail-empty and account sign-in states the shared ViewModel reports, with
/// their Add account and Sign in actions. The iPad reader column shows it; on iPhone a reader is never idle on screen.
/// </summary>
public sealed class IdlePageViewController(IdlePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<IdlePageViewModel>(viewModel, dispatcher, logger)
{
    private readonly MailIdleView _idle = new();

    public override void LoadView()
    {
        _idle.ActionInvoked += (_, _) => InvokeAction();
        var root = new UIView { BackgroundColor = WinoStyle.ZoneFill };
        WinoLayout.Fill(_idle, root);
        View = root;
        Apply();
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        Bind(nameof(ViewModel.IsMailEmptyStateVisible), vm => vm.IsMailEmptyStateVisible, _ => Apply());
        Bind(nameof(ViewModel.IsAccountStateVisible), vm => vm.IsAccountStateVisible, _ => Apply());
        Bind(nameof(ViewModel.AccountStateMessage), vm => vm.AccountStateMessage, _ => Apply());
        Bind(nameof(ViewModel.IsSignInVisible), vm => vm.IsSignInVisible, _ => Apply());
        return base.InitializeAsync(mode, parameter);
    }

    private void Apply()
    {
        if (ViewModel.IsMailEmptyStateVisible)
            _idle.SetContent(ViewModel.MailEmptyStateTitle, ViewModel.MailEmptyStateMessage, ViewModel.AddAccountText);
        else if (ViewModel.IsAccountStateVisible)
            _idle.SetContent(ViewModel.AccountStateTitle, ViewModel.AccountStateMessage, ViewModel.IsSignInVisible ? ViewModel.SignInText : null);
        else
            _idle.SetContent(Translator.NoMailSelected, Translator.MacOS_Reader_KeyboardHint, null);
    }

    private async void InvokeAction()
    {
        try
        {
            if (ViewModel.IsMailEmptyStateVisible) ViewModel.AddAccountCommand.Execute(null);
            else if (ViewModel.IsSignInVisible) await ViewModel.SignInCommand.ExecuteAsync(null);
        }
        catch (Exception exception) { ReportError(exception); }
    }

#if DEBUG
    internal string Dump() => _idle.Dump();
#endif
}
