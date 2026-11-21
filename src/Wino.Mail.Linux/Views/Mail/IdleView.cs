using Wino.Core.Domain;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reading pane while no message is open, and the mail states of <see cref="IdlePageViewModel"/>: an account that
/// needs a sign-in (or is loading its first folders), and no account at all. The pane keeps this view, so a later
/// navigation renavigates it with the new state.
/// </summary>
internal sealed class IdleView : WinoView<IdlePageViewModel>, IRenavigableView
{
    private readonly Adw.StatusPage _page = Adw.StatusPage.New();
    private readonly Gtk.Button _signIn;
    private readonly Gtk.Button _addAccount;
    private readonly Gtk.Button _manageAccounts;
    private readonly Adw.Spinner _loading = Adw.Spinner.New();

    public IdleView(IdlePageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        _signIn = Button(ViewModel.SignInText, suggested: true);
        _addAccount = Button(ViewModel.AddAccountText, suggested: true);
        _manageAccounts = Button(ViewModel.ManageAccountsText, suggested: false);
        _loading.SetSizeRequest(32, 32);
        _loading.SetHalign(Gtk.Align.Center);

        var actions = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        actions.SetHalign(Gtk.Align.Center);
        actions.Append(_loading);
        actions.Append(_signIn);
        actions.Append(_addAccount);
        actions.Append(_manageAccounts);
        _page.SetChild(actions);
        Root = _page;

        Bind.BindCommand(_signIn, ViewModel.SignInCommand);
        Bind.BindCommand(_addAccount, ViewModel.AddAccountCommand);
        Bind.BindCommand(_manageAccounts, ViewModel.ManageAccountsCommand);
        Bind.Bind(nameof(IdlePageViewModel.IsAccountStateVisible), static vm => vm.IsAccountStateVisible, _ => Apply());
        Bind.Bind(nameof(IdlePageViewModel.IsMailEmptyStateVisible), static vm => vm.IsMailEmptyStateVisible, _ => Apply());
        Bind.Bind(nameof(IdlePageViewModel.IsSignInVisible), static vm => vm.IsSignInVisible, _ => Apply());
        Bind.Bind(nameof(IdlePageViewModel.AccountStateTitle), static vm => vm.AccountStateTitle, _ => Apply());
        Bind.Bind(nameof(IdlePageViewModel.AccountStateMessage), static vm => vm.AccountStateMessage, _ => Apply());
        Subscribe<LanguageChanged>(_ => Apply());
        Apply();
    }

    public override Gtk.Widget Root { get; }

    public Task RenavigateAsync(object? parameter)
    {
        ViewModel.OnNavigatedTo(Core.Domain.Models.Navigation.NavigationMode.New, parameter);
        return Task.CompletedTask;
    }

    /// <summary>One line per state, for checks.</summary>
    internal string Describe()
        => $"title={_page.GetTitle()} signIn={_signIn.GetVisible()} loading={_loading.GetVisible()} addAccount={_addAccount.GetVisible()} manage={_manageAccounts.GetVisible()}";

    private void Apply()
    {
        var account = ViewModel.IsAccountStateVisible;
        var empty = !account && ViewModel.IsMailEmptyStateVisible;
        if (account)
        {
            _page.SetIconName(ViewModel.IsSignInVisible ? "dialog-password-symbolic" : null);
            _page.SetTitle(ViewModel.AccountStateTitle);
            _page.SetDescription(ViewModel.AccountStateMessage);
        }
        else if (empty)
        {
            _page.SetIconName("mail-unread-symbolic");
            _page.SetTitle(ViewModel.MailEmptyStateTitle);
            _page.SetDescription(ViewModel.MailEmptyStateMessage);
        }
        else
        {
            _page.SetIconName("mail-read-symbolic");
            _page.SetTitle(Translator.NoMailSelected);
            _page.SetDescription(null);
        }
        _signIn.SetVisible(account && ViewModel.IsSignInVisible);
        _loading.SetVisible(account && !ViewModel.IsSignInVisible);
        _addAccount.SetVisible(empty);
        _manageAccounts.SetVisible(empty);
        _signIn.SetLabel(ViewModel.SignInText);
        _addAccount.SetLabel(ViewModel.AddAccountText);
        _manageAccounts.SetLabel(ViewModel.ManageAccountsText);
    }

    private static Gtk.Button Button(string text, bool suggested)
    {
        var button = Gtk.Button.NewWithLabel(text);
        button.AddCssClass("pill");
        if (suggested) button.AddCssClass("suggested-action");
        return WinoA11y.Rename(button, text);
    }
}
