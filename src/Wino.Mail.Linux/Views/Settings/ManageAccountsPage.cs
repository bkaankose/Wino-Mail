using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Manage accounts (Windows AccountManagementPage, macOS order): Add account and the account-limit card, Link and
/// Reorder, the accounts with their icon, capabilities, address and attention state, then the account groups with
/// their accounts. A row opens the account details; a group opens the account group page.
/// </summary>
internal sealed class ManageAccountsPage(AccountManagementViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<AccountManagementViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _accounts;
    private WinoSettingsGroup? _groups;
    private WinoSettingsGroup? _empty;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        AddGroup(null, Settings.Command(Translator.Buttons_AddAccount, null, WinoIconGlyph.Add, vm.AddNewAccountCommand));
        Settings.Visible(PurchaseGroup().Root, vm, nameof(vm.IsPurchasePanelVisible), static s => s.IsPurchasePanelVisible);

        var manage = AddGroup(null,
            Settings.Command(Translator.SettingsLinkAccounts_Title, Translator.SettingsLinkAccounts_Description, WinoIconGlyph.PeopleLink, vm.CreateAccountGroupCommand),
            Settings.Command(Translator.SettingsReorderAccounts_Title, Translator.SettingsReorderAccounts_Description, WinoIconGlyph.ArrowSort, vm.ReorderAccountsCommand));
        Settings.Visible(manage.Root, vm, nameof(vm.HasAccountsDefined), static s => s.HasAccountsDefined);

        _accounts = Page.AddGroup(Translator.SettingsManageAccountSettings_Title);
        _groups = Page.AddGroup(Translator.AccountManagement_GroupsHeader);

        var empty = Adw.StatusPage.New();
        empty.SetTitle(Translator.SettingsNoAccountSetupMessage);
        empty.AddCssClass("compact");
        _empty = Page.AddGroup(null, null, WinoSettingsRows.Custom(empty, Translator.SettingsNoAccountSetupMessage));
        _empty.Root.SetVisible(false);

        Settings.Collection(vm.Accounts, Rebuild);
        WinoAppearance.Changed.Add(this, static page => page.Rebuild());
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    protected override Task DeactivateAsync()
    {
        WinoAppearance.Changed.Remove(this);
        return base.DeactivateAsync();
    }

    private WinoSettingsGroup PurchaseGroup()
    {
        var vm = ViewModel;
        var channels = WinoSettingsRows.Action(Translator.WinoUpgradeDescription, null, WinoIconGlyph.PersonSquare);
        Settings.Subtitle(channels, vm, nameof(vm.PurchaseChannelsDescription), static s => s.PurchaseChannelsDescription);

        var usage = WinoSettingsRows.Action(string.Empty);
        var meter = Gtk.ProgressBar.New();
        meter.SetValign(Gtk.Align.Center);
        meter.SetSizeRequest(160, -1);
        WinoA11y.Label(meter, Translator.WinoUpgradeDescription);
        WinoSettingsRows.Suffix(usage, meter);
        void Update()
        {
            usage.SetTitle(vm.UsedAccountsString);
            meter.SetFraction(vm.FREE_ACCOUNT_COUNT <= 0 ? 0 : Math.Min(1, (double)vm.UsedAccountCount / vm.FREE_ACCOUNT_COUNT));
            SetClass(meter, "error", vm.IsAccountCreationBlocked);
            SetClass(meter, "warning", !vm.IsAccountCreationBlocked && vm.IsAccountCreationAlmostOnLimit);
        }
        Settings.Bind(vm, nameof(vm.UsedAccountsString), static s => s.UsedAccountsString, _ => Update());
        Settings.Bind(vm, nameof(vm.IsAccountCreationBlocked), static s => s.IsAccountCreationBlocked, _ => Update());
        Settings.Bind(vm, nameof(vm.HasUnlimitedAccountProduct), static s => s.HasUnlimitedAccountProduct, _ => Update());

        var purchase = WinoSettingsRows.Action(Translator.WinoUpgradeMessage);
        WinoSettingsRows.Suffix(purchase, Settings.Button(Translator.Buttons_Purchase, vm.PurchaseUnlimitedAccountCommand, suggested: true, icon: WinoIconGlyph.Sparkle));
        return Page.AddGroup(null, null, channels, usage, purchase);
    }

    private void Rebuild()
    {
        if (_accounts is null || _groups is null || IsReleased) return;
        _rows?.Scope.Dispose();
        var rows = _rows = Settings.Child();
        _accounts.Clear();
        _groups.Clear();

        foreach (var item in ViewModel.Accounts.ToList())
        {
            switch (item)
            {
                case AccountProviderDetailViewModel account when account.Account is not null:
                    _accounts.Add(AccountRow(account, rows));
                    break;
                case MergedAccountProviderDetailViewModel merged when merged.MergedInbox is not null:
                    _groups.Add(GroupRow(merged, rows));
                    break;
            }
        }
        _empty?.Root.SetVisible(ViewModel.Accounts.Count == 0);
    }

    private Adw.ActionRow AccountRow(AccountProviderDetailViewModel item, SettingsBinder rows)
    {
        var row = WinoSettingsRows.Action(string.Empty);
        row.SetSubtitleLines(2);
        var icon = new WinoAccountIcon(32);
        row.AddPrefix(icon.Root);
        var attention = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        attention.AddCssClass("warning");
        attention.Append(new WinoIcon(WinoIconGlyph.Warning, 12).Root);
        var attentionText = Gtk.Label.New(Translator.Exception_AccountNeedsAttention_Title);
        attentionText.AddCssClass("caption");
        attention.Append(attentionText);
        WinoSettingsRows.Suffix(row, attention);
        var open = WinoSettingsRows.Suffix(row, WinoSettingsRows.ChevronButton(Translator.AccountContextMenu_ManageAccountSettings));
        row.SetActivatableWidget(open);
        rows.Own(GtkSignals.OnClicked(open, () =>
        {
            var command = ViewModel.NavigateAccountDetailsCommand;
            if (command.CanExecute(item)) command.Execute(item);
        }));

        rows.Bind(item, nameof(item.Account), static s => s.Account, account =>
        {
            var name = account?.Name ?? string.Empty;
            row.SetTitle(name);
            row.SetSubtitle(account is null ? string.Empty : item.DescriptionText ?? string.Empty);
            icon.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures);
            icon.AccessibleName = name;
            var needsAttention = account is not null && account.AttentionReason != AccountAttentionReason.None;
            attention.SetVisible(needsAttention);
            WinoA11y.Rename(open, $"{Translator.AccountContextMenu_ManageAccountSettings} {name}");
            WinoA11y.Rename(row, needsAttention ? $"{name}, {Translator.Exception_AccountNeedsAttention_Title}" : name);
        });
        return row;
    }

    private Adw.ExpanderRow GroupRow(MergedAccountProviderDetailViewModel merged, SettingsBinder rows)
    {
        var name = merged.MergedInbox.Name ?? string.Empty;
        var row = WinoSettingsRows.Expander(name, merged.DescriptionText);
        var tint = AccountGroupTint.Hex(merged.ColorKey);
        var icon = new WinoIcon(merged.IconGlyph, 20) { TintHex = tint };
        row.AddPrefix(icon.Root);
        var stack = new WinoAccountIconStack(24);
        stack.SetAccounts([.. merged.HoldingAccounts.Where(static account => account.Account is not null)
            .Select(account => (IAccountIconInfo)MailAccountIconInfoFactory.Create(account.Account, pictures))]);
        row.AddSuffix(stack.Root);

        var edit = rows.Command(Translator.SettingsEditLinkedInbox_Title, Translator.SettingsEditLinkedInbox_Description, WinoIconGlyph.Edit,
            ViewModel.EditMergedAccountsCommand, () => merged);
        row.AddRow(edit);
        foreach (var account in merged.HoldingAccounts)
        {
            if (account.Account is not null) row.AddRow(AccountRow(account, rows));
        }
        return row;
    }

    private static void SetClass(Gtk.Widget widget, string name, bool on)
    {
        if (on) widget.AddCssClass(name);
        else widget.RemoveCssClass(name);
    }
}
