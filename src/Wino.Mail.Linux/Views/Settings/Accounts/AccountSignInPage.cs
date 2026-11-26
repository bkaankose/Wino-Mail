using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// Sign-in and servers of an Outlook, Gmail or Exchange account (Windows AccountSignInPage, macOS
/// AccountSignInPageViewController): who is signed in with Sign in again, the Exchange password, then the Exchange
/// transport fixed at setup, shown read-only. IMAP and POP3 accounts use the server form instead. The navigation parameter is the account id.
/// </summary>
internal sealed class AccountSignInPage(AccountSignInPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<AccountSignInPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
#if DEBUG
    internal static AccountSignInPage? DebugCurrent { get; private set; }

    internal AccountSignInPageViewModel DebugViewModel => ViewModel;

    internal string DebugDescribe()
    {
        var vm = ViewModel;
        return $"page=AccountSignIn title={PageTitle} signedIn={vm.SignedInTitle}|{vm.Address} attention={vm.NeedsAttention} busy={vm.IsBusy} again={vm.IsSignInAgainVisible}"
               + $" exchange={vm.IsExchangeAccount} password={vm.IsExchangePasswordAuth} transport={vm.ExchangeTransportName}\n";
    }
#endif

    public string? PageTitle => ViewModel.Account is null ? Translator.AccountHub_SignInAndServersTitle : ViewModel.Account.Name;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        DebugCurrent = this;
        Settings.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
        }));
#endif
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var attention = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountHub_AttentionMessage) { IsClosable = false };
        Settings.Own(attention);
        var bars = Page.AddGroup();
        bars.HidesWhenEmpty = false;
        bars.Root.Add(attention.Root);
        Settings.Bind(vm, nameof(vm.NeedsAttention), static s => s.NeedsAttention, needed =>
        {
            attention.IsOpen = needed;
            bars.Root.SetVisible(needed);
        });

        var signedIn = WinoSettingsRows.Action(vm.SignedInTitle, vm.Address, WinoIconGlyph.LockClosed);
        Settings.Bind(vm, nameof(vm.SignedInTitle), static s => s.SignedInTitle, title => signedIn.SetTitle(title ?? string.Empty));
        Settings.Subtitle(signedIn, vm, nameof(vm.Address), static s => s.Address);
        var busy = WinoA11y.Label(Adw.Spinner.New(), Translator.ExchangeSetup_Checking);
        Settings.Visible(WinoSettingsRows.Suffix(signedIn, busy), vm, nameof(vm.IsBusy), static s => s.IsBusy);
        var again = WinoA11y.Rename(Settings.Button(Translator.ImapSetup_OAuthSignInAgain, vm.SignInAgainCommand), Translator.ImapSetup_OAuthSignInAgain);
        Settings.Visible(WinoSettingsRows.Suffix(signedIn, again), vm, nameof(vm.IsSignInAgainVisible), static s => s.IsSignInAgainVisible);
        Page.AddGroup(Translator.ImapSetup_SignInGroup, null, signedIn, BuildExchangePassword());

        var transport = WinoSettingsRows.Action(Translator.ExchangeSetup_TransportHeader, vm.ExchangeServerDescription, WinoIconGlyph.GlobeDesktop);
        Settings.Subtitle(transport, vm, nameof(vm.ExchangeServerDescription), static s => s.ExchangeServerDescription);
        WinoSettingsRows.Suffix(transport, Settings.LabelSuffix(vm, nameof(vm.ExchangeTransportName), static s => s.ExchangeTransportName));
        var servers = Page.AddGroup(Translator.AccountSignIn_ServersGroup, null, transport);
        Settings.Visible(servers.Root, vm, nameof(vm.Account), static s => s.IsExchangeAccount);
    }

    private Adw.ExpanderRow BuildExchangePassword()
    {
        var vm = ViewModel;
        var userName = WinoSettingsRows.Entry(Translator.ExchangeSetup_UserNameHeader);
        Bind.BindText(userName, nameof(vm.ExchangeUserName), static s => s.ExchangeUserName, static (s, v) => s.ExchangeUserName = v, coalesceWrites: true);
        var password = WinoSettingsRows.Password(Translator.ProviderSelection_PasswordHeader_Account);
        Bind.BindText(password, nameof(vm.ExchangePassword), static s => s.ExchangePassword, static (s, v) => s.ExchangePassword = v, coalesceWrites: true);
        var save = WinoSettingsRows.Action(string.Empty);
        WinoSettingsRows.Suffix(save, WinoA11y.Rename(Settings.Button(Translator.Buttons_Save, vm.UpdateExchangePasswordCommand, suggested: true), Translator.Buttons_Save));
        var expander = WinoSettingsRows.Expander(Translator.ProviderSelection_PasswordHeader_Account, Translator.AccountDetailsPage_ExchangePasswordDescription,
            WinoIconGlyph.Key, false, userName, password, save);
        Settings.Visible(expander, vm, nameof(vm.ServerInformation), static s => s.IsExchangePasswordAuth);
        return expander;
    }
}
