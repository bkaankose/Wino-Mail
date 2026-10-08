using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Manage accounts: Add account, the account-limit card (purchase channels, used/free meter and
/// purchase), the Merge inboxes and Reorder cards, account identity rows (profile picture or provider
/// icon like the shell pane, name, provider, capability summary and address, attention state, chevron
/// to account details) and merged inboxes (Windows AccountManagementPage).
/// </summary>
public sealed class ManageAccountsPageViewController(AccountManagementViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountManagementViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _accounts = new();
    private readonly WinoSettingsGroup _merged = new();
    private readonly NSTextField _empty = WinoStyle.Label(Translator.SettingsNoAccountSetupMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        Add(Row(Bind.Button(Translator.Buttons_AddAccount, vm.AddNewAccountCommand, primary: true, icon: WinoIconGlyph.Add)));
        Bind.Visible(Add(Group(null, PurchaseCard())), vm, nameof(vm.IsPurchasePanelVisible), s => s.IsPurchasePanelVisible);

        // Windows list header: Merge inboxes (with its "Mail only" scope tag) and Reorder, above the accounts.
        var merge = CommandCard(Translator.SettingsLinkAccounts_Title, Translator.SettingsLinkAccounts_Description, WinoIconGlyph.PeopleLink, vm.CreateMergedAccountCommand);
        merge.ShowsChevron = false;
        merge.Content = ScopeTag(Translator.SettingsLinkAccounts_MailOnlyScope);
        var reorder = CommandCard(Translator.SettingsReorderAccounts_Title, Translator.SettingsReorderAccounts_Description, WinoIconGlyph.ArrowSort, vm.ReorderAccountsCommand);
        reorder.ShowsChevron = false;
        Bind.Visible(Add(Group(null, merge, reorder)), vm, nameof(vm.HasAccountsDefined), s => s.HasAccountsDefined);

        _accounts.Title = Translator.SettingsManageAccountSettings_Title;
        Add(_accounts);
        _merged.Title = Translator.SettingsLinkAccounts_Title;
        Add(_merged);
        Add(_empty);

        Bind.Collection(vm.Accounts, RebuildAccounts);
    }

    /// <summary>
    /// Account limits (Windows PurchasePanel): what the add-on lifts, which purchase channels are open,
    /// the used/free count with its meter (amber one below the limit, red at the limit) and the purchase button.
    /// </summary>
    private WinoSettingsCard PurchaseCard()
    {
        var vm = ViewModel;
        var purchase = Bind.Button(Translator.WinoUpgradeMessage, vm.PurchaseUnlimitedAccountCommand, primary: true, icon: WinoIconGlyph.Sparkle);
        var card = Card(Translator.WinoUpgradeDescription, vm.PurchaseChannelsDescription, WinoIconGlyph.PersonSquare, purchase);
        Bind.Bind(vm, nameof(vm.PurchaseChannelsDescription), s => s.PurchaseChannelsDescription, text => card.Description = text);

        var usage = WinoStyle.Label(vm.UsedAccountsString, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var meter = new WinoBarView();
        WinoAccessibility.Label(meter, Translator.WinoUpgradeDescription);
        void UpdateUsage()
        {
            usage.StringValue = vm.UsedAccountsString;
            meter.Value = vm.FREE_ACCOUNT_COUNT <= 0 ? 0 : Math.Min(100, 100.0 * vm.UsedAccountCount / vm.FREE_ACCOUNT_COUNT);
            meter.ShowError = vm.IsAccountCreationBlocked;
            meter.Tint = vm.IsAccountCreationAlmostOnLimit ? WinoStyle.Caution : null;
            WinoAccessibility.Help(meter, vm.UsedAccountsString);
        }
        // Counts are computed properties raised together with the account list and the purchase state.
        Bind.Bind(vm, nameof(vm.UsedAccountsString), s => s.UsedAccountsString, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.IsAccountCreationBlocked), s => s.IsAccountCreationBlocked, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.HasUnlimitedAccountProduct), s => s.HasUnlimitedAccountProduct, _ => UpdateUsage());
        Bind.Collection(vm.Accounts, UpdateUsage);

        var bottom = WinoLayout.VStack(6, usage, meter);
        bottom.Alignment = NSLayoutAttribute.Leading;
        meter.WidthAnchor.ConstraintEqualTo(bottom.WidthAnchor).Active = true;
        card.BottomContent = bottom;
        return card;
    }

    /// <summary>The small bordered caption the Windows merge card shows ("Mail only").</summary>
    private static NSView ScopeTag(string text)
    {
        var label = WinoStyle.Label(text, NSFont.SystemFontOfSize(11), WinoStyle.SecondaryText);
        var tag = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, tag, 2, 8, 2, 8);
        return tag;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    private void RebuildAccounts()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _accounts.Clear();
        _merged.Clear();

        foreach (var item in ViewModel.Accounts.ToList())
        {
            switch (item)
            {
                case AccountProviderDetailViewModel account:
                    _accounts.Add(AccountRow(account, rows));
                    break;
                case MergedAccountProviderDetailViewModel merged:
                    _merged.Add(MergedRow(merged, rows));
                    break;
            }
        }

        _accounts.Hidden = _accounts.RowCount == 0;
        _merged.Hidden = _merged.RowCount == 0;
        _empty.Hidden = ViewModel.Accounts.Count > 0;
    }

    private WinoSettingsCard AccountRow(AccountProviderDetailViewModel account, SettingsBinder rows)
    {
        var card = new WinoSettingsCard(account.Account?.Name ?? string.Empty, account.DescriptionText)
        {
            IsClickable = true
        };
        card.Content = AccountStatus(account.Account);
        var icon = AccountIcon(account.Account);
        card.LeadingView = icon;
        rows.Bind(account, nameof(account.Account), a => a.Account, value =>
        {
            card.Header = value?.Name ?? string.Empty;
            card.Description = account.DescriptionText;
            ConfigureIcon(icon, value);
            card.Content = AccountStatus(value);
        });
        EventHandler handler = (_, _) =>
        {
            var command = ViewModel.NavigateAccountDetailsCommand;
            if (command.CanExecute(account)) command.Execute(account);
        };
        card.Activated += handler;
        rows.Scope.Own(new SettingsDisposable(() => card.Activated -= handler));
        WinoAccessibility.Help(card, account.Account?.Address);
        return card;
    }

    private WinoSettingsExpander MergedRow(MergedAccountProviderDetailViewModel merged, SettingsBinder rows)
    {
        var expander = new WinoSettingsExpander(merged.MergedInbox?.Name ?? string.Empty, merged.AccountAddresses, WinoIconGlyph.People);
        var edit = new WinoSettingsCard(Translator.SettingsEditLinkedInbox_Title, Translator.SettingsEditLinkedInbox_Description, WinoIconGlyph.Link)
        {
            IsClickable = true
        };
        EventHandler handler = (_, _) =>
        {
            var command = ViewModel.EditMergedAccountsCommand;
            if (command.CanExecute(merged)) command.Execute(merged);
        };
        edit.Activated += handler;
        rows.Scope.Own(new SettingsDisposable(() => edit.Activated -= handler));
        expander.Add(edit);
        foreach (var account in merged.HoldingAccounts) expander.Add(AccountRow(account, rows));
        return expander;
    }

    private static NSView? AccountStatus(MailAccount? account)
    {
        if (account is null || account.AttentionReason == AccountAttentionReason.None) return null;
        var dot = new WinoIconView(WinoIconGlyph.Warning, 11, WinoStyle.Caution);
        var text = WinoStyle.Label(Translator.Exception_AccountNeedsAttention_Title, WinoStyle.Caption, WinoStyle.Caution);
        return WinoLayout.HStack(4, dot, text);
    }

    private WinoAccountIconView AccountIcon(MailAccount? account)
    {
        var icon = new WinoAccountIconView(32);
        ConfigureIcon(icon, account);
        return icon;
    }

    /// <summary>The same icon the shell account rows show: the stored profile picture, else the provider glyph.</summary>
    private void ConfigureIcon(WinoAccountIconView icon, MailAccount? account)
        => icon.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures);
}
