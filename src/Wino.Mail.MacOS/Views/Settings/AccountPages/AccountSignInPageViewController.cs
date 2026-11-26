using System.Linq;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>Sign-in and servers of an Outlook, Gmail or Exchange account (Windows AccountSignInPage).</summary>
public sealed class AccountSignInPageViewController(AccountSignInPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountSignInPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    public string? PageTitle => ViewModel.Account is null ? Translator.AccountHub_SignInAndServersTitle : ViewModel.Account.Name;

    public event System.EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, System.EventArgs.Empty));

        var attention = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountHub_AttentionMessage);
        attention.IsClosable = false;
        Bind.Visible(attention, vm, nameof(vm.NeedsAttention), s => s.NeedsAttention);
        Add(attention);

        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(vm, nameof(vm.IsBusy), s => s.IsBusy, busy =>
        {
            spinner.Hidden = !busy;
            if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });
        var again = Bind.Button(Translator.ImapSetup_OAuthSignInAgain, vm.SignInAgainCommand);
        Bind.Visible(again, vm, nameof(vm.IsSignInAgainVisible), s => s.IsSignInAgainVisible);
        var signedIn = Card(Translator.ImapSetup_SignInGroup, vm.Address, WinoIconGlyph.LockClosed, Row(spinner, again));
        Bind.Bind(vm, nameof(vm.SignedInTitle), s => s.SignedInTitle, title => signedIn.Header = title);
        Bind.Bind(vm, nameof(vm.Address), s => s.Address, address => signedIn.Description = address);
        AddGroup(Translator.ImapSetup_SignInGroup, signedIn, BuildExchangePassword());

        var servers = AddGroup(Translator.AccountSignIn_ServersGroup, BuildExchangeTransport());
        Bind.Visible(servers, vm, nameof(vm.IsExchangeAccount), s => s.IsExchangeAccount);
    }

    private WinoSettingsExpander BuildExchangePassword()
    {
        var vm = ViewModel;
        var userName = Bind.TextField(vm, nameof(vm.ExchangeUserName), s => s.ExchangeUserName, (s, v) => s.ExchangeUserName = v, vm.Address, 240);
        WinoAccessibility.Label(userName, Translator.ExchangeSetup_UserNameHeader);
        WinoAccessibility.Help(userName, Translator.ExchangeSetup_UserNameDescription);
        Bind.Bind(vm, nameof(vm.Address), s => s.Address, address => userName.PlaceholderString = address ?? string.Empty);

        var password = new NSSecureTextField { Font = WinoStyle.Body, PlaceholderString = Translator.ProviderSelection_PasswordHeader_Account, TranslatesAutoresizingMaskIntoConstraints = false };
        password.WidthAnchor.ConstraintEqualTo(240).Active = true;
        WinoAccessibility.Label(password, Translator.ProviderSelection_PasswordHeader_Account);
        Bind.Bind(vm, nameof(vm.ExchangePassword), s => s.ExchangePassword, value =>
        {
            if (password.StringValue != (value ?? string.Empty)) password.StringValue = value ?? string.Empty;
        });
        System.EventHandler passwordChanged = (_, _) => vm.ExchangePassword = password.StringValue;
        password.Changed += passwordChanged;
        Bindings.Own(new ActionDisposable(() => password.Changed -= passwordChanged));

        var save = Bind.Button(Translator.Buttons_Save, vm.UpdateExchangePasswordCommand, primary: true);
        var fields = WinoLayout.VStack(WinoStyle.Space2, userName, password, save);
        fields.Alignment = NSLayoutAttribute.Trailing;
        var expander = Expander(Translator.ProviderSelection_PasswordHeader_Account, Translator.AccountDetailsPage_ExchangePasswordDescription, WinoIconGlyph.Key, null,
            Card(string.Empty, null, WinoIconGlyph.None, fields));
        Bind.Visible(expander, vm, nameof(vm.IsExchangePasswordAuth), s => s.IsExchangePasswordAuth);
        return expander;
    }

    private WinoSettingsCard BuildExchangeTransport()
    {
        var vm = ViewModel;
        var transport = WinoStyle.Label(vm.ExchangeTransportName, WinoStyle.Body, WinoStyle.SecondaryText);
        transport.Selectable = true;
        Bind.Bind(vm, nameof(vm.ExchangeTransportName), s => s.ExchangeTransportName, name => transport.StringValue = name ?? string.Empty);
        var card = Card(Translator.ExchangeSetup_TransportHeader, null, WinoIconGlyph.GlobeDesktop, transport);
        Bind.Bind(vm, nameof(vm.ExchangeServerDescription), s => s.ExchangeServerDescription, text => card.Description = text);
        return card;
    }
}
