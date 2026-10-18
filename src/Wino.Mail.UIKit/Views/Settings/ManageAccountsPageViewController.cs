using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Manage accounts (Mac ManageAccountsPageViewController, Windows AccountManagementPage): Add account as the navigation
/// bar's "+", the account-limit card on the page (purchase channels, used/free meter and purchase), the Merge inboxes and Reorder cards, account
/// identity rows (profile picture or provider icon, name, capability summary, attention state, chevron to account
/// details) and linked inboxes. Reorder runs the shared command, which opens the account reorder dialog.
/// </summary>
public sealed class ManageAccountsPageViewController(AccountManagementViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountManagementViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _accounts = new();
    private readonly WinoSettingsGroup _merged = new();
    private readonly UILabel _empty = WinoStyle.Label(Translator.SettingsNoAccountSetupMessage, WinoStyle.Body, WinoStyle.SecondaryText, 0);
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Add account is the navigation bar's "+" (18 Oct 2026); the account-limit card stays on the page itself.
        BarButton(WinoIconGlyph.Add, Translator.Buttons_AddAccount, vm.AddNewAccountCommand);
        Bind.Visible(Add(PurchaseCard()), vm, nameof(vm.IsPurchasePanelVisible), s => s.IsPurchasePanelVisible);

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
    /// Account limits (Windows PurchasePanel) as one surface on the page, not behind a push (18 Oct 2026): the add-on
    /// name, the free-account sentence, the open purchase channels, the used/free count with its meter (amber one below
    /// the limit, red at the limit) and the purchase button.
    /// </summary>
    private UIView PurchaseCard()
    {
        var vm = ViewModel;
        var purchase = Bind.Button(Translator.WinoUpgradeMessage, vm.PurchaseUnlimitedAccountCommand, primary: true, icon: WinoIconGlyph.Sparkle);
        purchase.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        // A short title keeps the card readable at phone width; the free-account sentence and the purchase channels follow
        // as the description and a caption (Windows shows the long sentence as the title on a wide pane).
        var title = WinoStyle.Label(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 0);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var description = WinoStyle.Label(Translator.WinoUpgradeDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var channels = WinoStyle.Label(vm.PurchaseChannelsDescription, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        Bind.Bind(vm, nameof(vm.PurchaseChannelsDescription), s => s.PurchaseChannelsDescription, text =>
        {
            channels.Text = text;
            channels.Hidden = string.IsNullOrWhiteSpace(text);
        });

        var usage = WinoStyle.Label(vm.UsedAccountsString, WinoStyle.Caption, WinoStyle.SecondaryText);
        var meter = new UIProgressView(UIProgressViewStyle.Default) { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(meter, Translator.WinoUpgradeDescription);
        void UpdateUsage()
        {
            usage.Text = vm.UsedAccountsString;
            var value = vm.FREE_ACCOUNT_COUNT <= 0 ? 0 : Math.Min(1, (double)vm.UsedAccountCount / vm.FREE_ACCOUNT_COUNT);
            meter.Progress = (float)value;
            meter.ProgressTintColor = vm.IsAccountCreationBlocked ? WinoStyle.Critical : vm.IsAccountCreationAlmostOnLimit ? WinoStyle.Caution : WinoStyle.Accent;
            meter.AccessibilityValue = vm.UsedAccountsString;
        }
        // Counts are computed properties raised together with the account list and the purchase state.
        Bind.Bind(vm, nameof(vm.UsedAccountsString), s => s.UsedAccountsString, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.IsAccountCreationBlocked), s => s.IsAccountCreationBlocked, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.HasUnlimitedAccountProduct), s => s.HasUnlimitedAccountProduct, _ => UpdateUsage());
        Bind.Collection(vm.Accounts, UpdateUsage);

        var content = WinoLayout.VStack(6, title, description, channels, usage, meter, purchase);
        content.Alignment = UIStackViewAlignment.Fill;
        content.SetCustomSpacing(2, title);
        content.SetCustomSpacing(10, channels);
        content.SetCustomSpacing(12, meter);
        var card = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, card, 12, WinoSettingsStyle.CardPadding, 12, WinoSettingsStyle.CardPadding);
        return card;
    }

    /// <summary>The small bordered caption the Windows merge card shows ("Mail only").</summary>
    private static UIView ScopeTag(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        var tag = new WinoSurfaceView { Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 4, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, tag, 2, 8, 2, 8);
        return tag;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

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
            WinoAccessibility.Help(card, value?.Address);
        });
        EventHandler handler = (_, _) =>
        {
            var command = ViewModel.NavigateAccountDetailsCommand;
            if (command.CanExecute(account)) command.Execute(account);
        };
        card.Activated += handler;
        rows.Scope.Own(new SettingsDisposable(() => card.Activated -= handler));
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

    private static UIView? AccountStatus(MailAccount? account)
    {
        if (account is null || account.AttentionReason == AccountAttentionReason.None) return null;
        var dot = new WinoIconView(WinoIconGlyph.Warning, 11, WinoStyle.Caution);
        WinoLayout.Size(dot, 11, 11);
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
