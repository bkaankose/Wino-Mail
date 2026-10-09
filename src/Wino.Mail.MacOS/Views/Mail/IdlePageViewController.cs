using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Reading pane idle page (Windows IdlePage): "no message selected" with keyboard hints, or the
/// mail-empty and account sign-in states the shared ViewModel reports.
/// </summary>
public sealed class IdlePageViewController(IdlePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<IdlePageViewModel>(viewModel, dispatcher, logger)
{
    private MailIdleView _idle = null!;

    public override void LoadView()
    {
        _idle = new MailIdleView();
        _idle.ActionInvoked += (_, _) => InvokeAction();
        var root = new NSView();
        WinoLayout.Fill(_idle, root);
        View = root;
    }

    protected override Task InitializeAsync(Wino.Core.Domain.Models.Navigation.NavigationMode mode, object? parameter)
    {
        Bindings.Own(new PropertyBinding<IdlePageViewModel, bool>(ViewModel, nameof(ViewModel.IsMailEmptyStateVisible), vm => vm.IsMailEmptyStateVisible, _ => Apply(), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<IdlePageViewModel, bool>(ViewModel, nameof(ViewModel.IsAccountStateVisible), vm => vm.IsAccountStateVisible, _ => Apply(), Dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<IdlePageViewModel, string>(ViewModel, nameof(ViewModel.AccountStateMessage), vm => vm.AccountStateMessage, _ => Apply(), Dispatcher, ReportError));
        return base.InitializeAsync(mode, parameter);
    }

    private void Apply()
    {
        if (ViewModel.IsMailEmptyStateVisible)
            _idle.SetContent(ViewModel.MailEmptyStateTitle, ViewModel.MailEmptyStateMessage, ViewModel.AddAccountText);
        else if (ViewModel.IsAccountStateVisible)
            _idle.SetContent(ViewModel.AccountStateTitle, ViewModel.AccountStateMessage, ViewModel.IsSignInVisible ? ViewModel.SignInText : null);
        else
            // No translation key exists for the Mac keyboard hints yet.
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
}
