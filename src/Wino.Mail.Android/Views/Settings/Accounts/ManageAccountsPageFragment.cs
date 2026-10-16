using Android.Views;
using Android.Widget;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Manage accounts (iOS ManageAccountsPageViewController, Windows AccountManagementPage): Add account (the shared
/// command opens onboarding), the account-limit card, the Merge inboxes and Reorder cards, the account identity rows
/// (profile picture or provider icon, name, capability summary, attention state; a tap opens account details) and linked
/// inboxes. Android v1 has no store billing (IPlatformCapabilities.MicrosoftStore and AppleAppStore are false), so the
/// account-limit card with its purchase button is not shown; the shared AddNewAccount command still explains the limit.
/// Reorder runs the shared command, which opens the account reorder dialog.
/// </summary>
public sealed class ManageAccountsPageFragment : SettingsPageFragment<AccountManagementViewModel>
{
    private WinoSettingsGroup? _accounts;
    private WinoSettingsGroup? _merged;
    private TextView? _empty;
    private BindingScope? _rowScope;
    private IPictureStorageService? _pictures;

    protected override void BuildPage()
    {
        var vm = VM;
        var services = MainApplication.Services;
        _pictures = services?.GetService<IPictureStorageService>();

        AddGroup(null, ActionCard(Translator.Buttons_AddAccount, vm.AddNewAccountCommand));

        // Purchase entry points exist only where the platform sells (IPlatformCapabilities.Purchases, in the ViewModel).
        Bind.Visible(Add(Group(null, PurchaseCard())), vm, nameof(vm.IsPurchasePanelVisible), s => s.IsPurchasePanelVisible);

        // Windows list header: Merge inboxes (with its "Mail only" scope tag) and Reorder, above the accounts.
        var merge = CommandCard(Translator.SettingsLinkAccounts_Title, Translator.SettingsLinkAccounts_Description, WinoIconGlyph.PeopleLink, vm.CreateMergedAccountCommand);
        merge.Content = ScopeTag(Translator.SettingsLinkAccounts_MailOnlyScope);
        var reorder = CommandCard(Translator.SettingsReorderAccounts_Title, Translator.SettingsReorderAccounts_Description, WinoIconGlyph.ArrowSort, vm.ReorderAccountsCommand);
        Bind.Visible(Add(Group(null, merge, reorder)), vm, nameof(vm.HasAccountsDefined), s => s.HasAccountsDefined);

        _accounts = Add(Group(Translator.SettingsManageAccountSettings_Title));
        _merged = Add(Group(Translator.SettingsLinkAccounts_Title));
        _empty = Add(Text(Translator.SettingsNoAccountSetupMessage, WinoTextStyle.BodySecondary));

        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        Bind.Collection(vm.Accounts, RebuildAccounts);
    }

    /// <summary>
    /// Account limits (Windows PurchasePanel): what the add-on lifts, the purchase channels, the used/free count with its
    /// meter (amber one below the limit, red at the limit) and the purchase button under the text.
    /// </summary>
    private WinoSettingsCard PurchaseCard()
    {
        var vm = VM;
        var purchase = Bind.Button(Translator.WinoUpgradeMessage, vm.PurchaseUnlimitedAccountCommand, primary: true, icon: WinoIconGlyph.Sparkle);
        var card = Card(Translator.WinoAddOn_UNLIMITED_ACCOUNTS_Name, Translator.WinoUpgradeDescription, WinoIconGlyph.PersonSquare);
        var channels = Caption(vm.PurchaseChannelsDescription);
        Bind.Bind(vm, nameof(vm.PurchaseChannelsDescription), s => s.PurchaseChannelsDescription, text =>
        {
            channels.Text = text;
            channels.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
        });

        var usage = Caption(vm.UsedAccountsString);
        var meter = new LinearProgressIndicator(Ctx) { Max = 100 };
        void UpdateUsage()
        {
            usage.Text = vm.UsedAccountsString;
            var value = vm.FREE_ACCOUNT_COUNT <= 0 ? 0 : Math.Min(1, (double)vm.UsedAccountCount / vm.FREE_ACCOUNT_COUNT);
            meter.Progress = (int)Math.Round(value * 100);
            var color = vm.IsAccountCreationBlocked ? WinoColors.Critical(Ctx) : vm.IsAccountCreationAlmostOnLimit ? WinoColors.Caution(Ctx) : WinoColors.Accent(Ctx);
            meter.SetIndicatorColor(color.ToArgb());
            meter.ContentDescription = $"{Translator.WinoUpgradeDescription}, {vm.UsedAccountsString}";
        }
        // Counts are computed properties raised together with the account list and the purchase state.
        Bind.Bind(vm, nameof(vm.UsedAccountsString), s => s.UsedAccountsString, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.IsAccountCreationBlocked), s => s.IsAccountCreationBlocked, _ => UpdateUsage());
        Bind.Bind(vm, nameof(vm.HasUnlimitedAccountProduct), s => s.HasUnlimitedAccountProduct, _ => UpdateUsage());
        Bind.Collection(vm.Accounts, UpdateUsage);

        var bottom = Stack(Resource.Dimension.wino_space_xs, channels, usage, meter, Row(purchase));
        card.BottomContent = bottom;
        return card;
    }

    /// <summary>The small bordered caption the Windows merge card shows ("Mail only").</summary>
    private TextView ScopeTag(string text)
    {
        var tag = Caption(text);
        WinoTokens.Pad(tag, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs);
        tag.Background = AccountSettingsParts.Surface(Ctx, WinoColors.CardFill(Ctx), WinoColors.CardStroke(Ctx));
        return tag;
    }

    private void RebuildAccounts()
    {
        if (_accounts is null || _merged is null || _empty is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _accounts.Clear();
        _merged.Clear();

        foreach (var item in VM.Accounts.ToList())
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

        _accounts.Visibility = _accounts.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
        _merged.Visibility = _merged.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
        _empty.Visibility = VM.Accounts.Count > 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private WinoSettingsCard AccountRow(AccountProviderDetailViewModel account, SettingsBinder rows)
    {
        var card = new WinoSettingsCard(Ctx, account.Account?.Name ?? string.Empty, account.DescriptionText) { IsClickable = true, ShowsChevron = true };
        var icon = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_size_swatch_large) };
        card.LeadingView = icon;
        rows.Bind(account, nameof(account.Account), a => a.Account, value =>
        {
            card.Header = value?.Name ?? string.Empty;
            card.Description = account.DescriptionText;
            ConfigureIcon(icon, value);
            card.Content = AccountStatus(value);
            card.ContentDescription = value is null || string.IsNullOrEmpty(value.Address) ? value?.Name : $"{value.Name}, {value.Address}";
        });
        EventHandler handler = (_, _) => AccountSettingsParts.Execute(VM.NavigateAccountDetailsCommand, account, ReportError);
        card.Activated += handler;
        rows.Scope.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    private WinoSettingsExpander MergedRow(MergedAccountProviderDetailViewModel merged, SettingsBinder rows)
    {
        var expander = new WinoSettingsExpander(Ctx, merged.MergedInbox?.Name ?? string.Empty, merged.AccountAddresses, WinoIconGlyph.People);
        var edit = new WinoSettingsCard(Ctx, Translator.SettingsEditLinkedInbox_Title, Translator.SettingsEditLinkedInbox_Description, WinoIconGlyph.Link)
        {
            IsClickable = true,
            ShowsChevron = true
        };
        EventHandler handler = (_, _) => AccountSettingsParts.Execute(VM.EditMergedAccountsCommand, merged, ReportError);
        edit.Activated += handler;
        rows.Scope.Own(new ActionDisposable(() => edit.Activated -= handler));
        expander.Add(edit);
        foreach (var account in merged.HoldingAccounts) expander.Add(AccountRow(account, rows));
        return expander;
    }

    private View? AccountStatus(MailAccount? account)
    {
        if (account is null || account.AttentionReason == AccountAttentionReason.None) return null;
        var caution = WinoColors.Caution(Ctx);
        var row = AccountSettingsParts.HRow(Ctx);
        row.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Warning, Resource.Dimension.wino_glyph_xs, caution, colorful: false));
        row.AddView(AccountSettingsParts.Label(Ctx, Translator.Exception_AccountNeedsAttention_Title, WinoTextStyle.Caption, caution, maxLines: 2),
            AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_2xs));
        return row;
    }

    /// <summary>The same icon the shell account rows show: the stored profile picture, else the provider glyph.</summary>
    private void ConfigureIcon(WinoAccountIconView icon, MailAccount? account)
    {
        icon.DisplayName = account?.Name;
        icon.Account = account is null || _pictures is null ? null : MailAccountIconInfoFactory.Create(account, _pictures);
    }
}
