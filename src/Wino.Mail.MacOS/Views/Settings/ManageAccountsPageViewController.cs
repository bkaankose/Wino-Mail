using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Extras;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Manage accounts: Add account, the account-limit card (purchase channels, used/free meter and
/// purchase), the Create account group and Reorder cards, account identity rows (profile picture or provider
/// icon like the shell pane, name, provider, capability summary and address, attention state, chevron
/// to account details) and account groups (Windows AccountManagementPage).
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

        // Windows header row: the square Add account tile, then the account-limit card filling the rest of the row.
        // Narrow windows stack the card under the tile.
        var purchase = Bind.Visible(PurchasePanel(), vm, nameof(vm.IsPurchasePanelVisible), s => s.IsPurchasePanelVisible);
        purchase.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var header = Add(new SettingsAdaptiveRow(HeaderBreakpoint, 12, NSLayoutAttribute.Top, null, AddAccountTile(), purchase));
        purchase.WidthAnchor.ConstraintLessThanOrEqualTo(header.WidthAnchor).Active = true;

        // Windows list header: Create account group and Reorder, above the accounts.
        var merge = CommandCard(Translator.SettingsLinkAccounts_Title, Translator.SettingsLinkAccounts_Description, WinoIconGlyph.PeopleLink, vm.CreateAccountGroupCommand);
        merge.ShowsChevron = false;
        var reorder = CommandCard(Translator.SettingsReorderAccounts_Title, Translator.SettingsReorderAccounts_Description, WinoIconGlyph.ArrowSort, vm.ReorderAccountsCommand);
        reorder.ShowsChevron = false;
        Bind.Visible(Add(Group(null, merge, reorder)), vm, nameof(vm.HasAccountsDefined), s => s.HasAccountsDefined);

        _accounts.Title = Translator.SettingsManageAccountSettings_Title;
        Add(_accounts);
        _merged.Title = Translator.AccountManagement_GroupsHeader;
        Add(_merged);
        Add(_empty);

        Bind.Collection(vm.Accounts, RebuildAccounts);
    }

    /// <summary>The Windows square Add account button: the plus above its label, centred together in the tile.</summary>
    private NSView AddAccountTile()
    {
        var vm = ViewModel;
        var tile = new WinoPressableView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var content = WinoLayout.VStack(12,
            new WinoIconView(WinoIconGlyph.Add, 46, WinoStyle.PrimaryText),
            WinoStyle.Label(Translator.Buttons_AddAccount, WinoStyle.BodyStrong, WinoStyle.PrimaryText));
        content.Alignment = NSLayoutAttribute.CenterX;
        tile.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([content.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor), content.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor)]);
        WinoLayout.Size(tile, TileSize, TileSize);
        WinoAccessibility.Label(tile, Translator.Buttons_AddAccount);
        tile.AccessibilityIdentifier = "AccountManagementPageAddAccountTile";
        tile.Clicked += (_, _) => { if (vm.AddNewAccountCommand.CanExecute(null)) vm.AddNewAccountCommand.Execute(null); };
        return tile;
    }

    private const double TileSize = 200;

    /// <summary>Above the side-by-side minimum: the tile, the gap and the card's widest bottom line.</summary>
    private const double HeaderBreakpoint = 560;

    /// <summary>
    /// Account limits (Windows PurchasePanel): the free-account heading, which purchase channels are open, the used/free
    /// count with its meter (amber one below the limit, red at the limit) and the accent purchase button at the bottom
    /// right. App Store builds add the price and Restore Purchases, since Unlimited Accounts is bought here signed out.
    /// </summary>
    private NSView PurchasePanel()
    {
        var vm = ViewModel;

        var heading = WinoLayout.HStack(12,
            new WinoIconView(WinoIconGlyph.PersonSquare, 32),
            WinoStyle.Label(Translator.WinoUpgradeDescription, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0));
        heading.Alignment = NSLayoutAttribute.CenterY;

        var channels = Bind.Label(vm, nameof(vm.PurchaseChannelsDescription), s => s.PurchaseChannelsDescription, WinoStyle.Caption, WinoStyle.SecondaryText, 0);

        var usage = WinoStyle.Label(vm.UsedAccountsString, WinoStyle.Caption, WinoStyle.PrimaryText);
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
        var meterStack = WinoLayout.VStack(12, usage, meter);
        meterStack.Alignment = NSLayoutAttribute.Leading;
        meter.WidthAnchor.ConstraintEqualTo(meterStack.WidthAnchor).Active = true;

        var purchase = Bind.Button(Translator.WinoUpgradeMessage, vm.PurchaseUnlimitedAccountCommand, primary: true, icon: WinoIconGlyph.Sparkle);
        purchase.SetValueForKey(new Foundation.NSString("AccountManagementPagePurchaseButton"), new Foundation.NSString("accessibilityIdentifier"));
        // Two lines, so the card stays narrow enough to sit beside the tile: price and restore, then the purchase.
        var footer = WinoLayout.VStack(8);
        footer.Alignment = NSLayoutAttribute.Trailing;
        if (vm.IsAppStoreRestoreAvailable)
        {
            var store = WinoLayout.HStack(12,
                Bind.Label(vm, nameof(vm.AppStoreUnlimitedAccountsPriceText), s => s.AppStoreUnlimitedAccountsPriceText, WinoStyle.Caption, WinoStyle.SecondaryText),
                WinoLayout.Spacer(),
                Bind.Button(Translator.WinoAccount_Management_RestorePurchases, vm.RestoreAppStorePurchasesCommand));
            store.Alignment = NSLayoutAttribute.CenterY;
            footer.AddArrangedSubview(store);
            store.WidthAnchor.ConstraintEqualTo(footer.WidthAnchor).Active = true;
        }
        footer.AddArrangedSubview(purchase);

        var stack = WinoLayout.VStack(6, heading, channels, WinoLayout.Spacer(), meterStack, WinoLayout.Spacer(), footer);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.SetCustomSpacing(12, meterStack);
        foreach (var view in new NSView[] { channels, meterStack, footer }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;

        var card = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(stack, card, WinoSettingsStyle.CardPadding);
        card.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)TileSize).Active = true;
        card.AccessibilityIdentifier = "AccountManagementPagePurchasePanel";
        return card;
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
        var apps = AppIcons(account, rows);
        card.Content = AccountRowContent(apps, account.Account);
        var icon = AccountIcon(account.Account);
        card.LeadingView = icon;
        rows.Bind(account, nameof(account.Account), a => a.Account, value =>
        {
            card.Header = value?.Name ?? string.Empty;
            card.Description = account.DescriptionText;
            ConfigureIcon(icon, value);
            card.Content = AccountRowContent(apps, value);
        });
        EventHandler handler = (_, _) =>
        {
            var command = ViewModel.NavigateAccountDetailsCommand;
            if (command.CanExecute(account)) command.Execute(account);
        };
        card.Activated += handler;
        rows.Scope.Own(new SettingsDisposable(() => card.Activated -= handler));
        WinoAccessibility.Help(card, account.Account?.DisplayAddress);
        return card;
    }

    private WinoSettingsExpander MergedRow(MergedAccountProviderDetailViewModel merged, SettingsBinder rows)
    {
        // Windows MergedAccountTemplate: the group icon and name in the group color, then what the group does.
        var expander = new WinoSettingsExpander(merged.MergedInbox?.Name ?? string.Empty, merged.DescriptionText);
        var color = WinoStyle.AccountGroupColor(merged.ColorKey);
        var icon = new WinoIconView(merged.IconGlyph, 20, color) { Colorful = false };
        WinoLayout.Size(icon, 32, 32);
        expander.HeaderCard.LeadingView = icon;
        expander.HeaderCard.HeaderColor = color;
        var edit = new WinoSettingsCard(Translator.SettingsEditLinkedInbox_Title, Translator.SettingsEditLinkedInbox_Description, WinoIconGlyph.Edit)
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

    /// <summary>The apps this account provides, read-only (Windows AccountManagementAppIcons); they are switched on the account's page.</summary>
    private static NSStackView AppIcons(AccountProviderDetailViewModel account, SettingsBinder rows)
    {
        var icons = WinoLayout.HStack(8);
        icons.Alignment = NSLayoutAttribute.CenterY;
        AddAppIcon(icons, rows, account, WinoIconGlyph.Mail, nameof(account.IsMailOn), a => a.IsMailOn);
        AddAppIcon(icons, rows, account, WinoIconGlyph.Calendar, nameof(account.IsCalendarOn), a => a.IsCalendarOn);
        AddAppIcon(icons, rows, account, WinoIconGlyph.People, nameof(account.IsPeopleOn), a => a.IsPeopleOn);
        AddAppIcon(icons, rows, account, WinoIconGlyph.CheckmarkCircle, nameof(account.IsToDoOn), a => a.IsToDoOn);
        rows.Bind(account, nameof(account.AppsAutomationName), a => a.AppsAutomationName, name => WinoAccessibility.Label(icons, name));
        rows.Bind(account, nameof(account.CapabilitySummary), a => a.CapabilitySummary, summary => icons.ToolTip = summary);
        return icons;
    }

    private static void AddAppIcon(NSStackView icons, SettingsBinder rows, AccountProviderDetailViewModel account, WinoIconGlyph glyph, string property, Func<AccountProviderDetailViewModel, bool> read)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        WinoLayout.Size(icon, 16, 16);
        icons.AddArrangedSubview(icon);
        rows.Bind(account, property, read, on => icon.Hidden = !on);
    }

    private static NSView? AccountRowContent(NSStackView apps, MailAccount? account)
    {
        var status = AccountStatus(account);
        return status is null ? apps : WinoLayout.HStack(12, apps, status);
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
