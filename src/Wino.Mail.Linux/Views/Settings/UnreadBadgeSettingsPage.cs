using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>Unread badges (Windows UnreadBadgeSettingsPage): one row per account leading to its badge settings.</summary>
internal sealed class UnreadBadgeSettingsPage(UnreadBadgeSettingsPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<UnreadBadgeSettingsPageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _accounts;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        // The group description explains the app icon badge, which Linux does not have.
        _accounts = Page.AddGroup(Translator.UnreadBadges_Accounts_Title);
        Settings.Collection(vm.Accounts, RebuildAccounts);

        if (!vm.IsLaunchNavigationAvailable) return;
        Page.AddGroup(Translator.UnreadBadges_LaunchBehaviour_Title, null,
            Settings.Switch(vm, Translator.UnreadBadges_Launch_Title, Translator.UnreadBadges_Launch_Description, WinoIconGlyph.Open,
                nameof(vm.IsLaunchNavigationEnabled), static s => s.IsLaunchNavigationEnabled, static (s, v) => s.IsLaunchNavigationEnabled = v));
    }

    private void RebuildAccounts()
    {
        if (_accounts is null) return;
        _rows?.Scope.Dispose();
        _accounts.Clear();
        var rows = _rows = Settings.Child();
        foreach (var account in ViewModel.Accounts.ToList())
        {
            var name = account.AccountName ?? string.Empty;
            var description = string.IsNullOrWhiteSpace(account.CountSourceDescription)
                ? account.DescriptionText
                : $"{account.DescriptionText}\n{account.CountSourceDescription}";
            var row = WinoSettingsRows.Action(name, description);
            var icon = new WinoAccountIcon(28) { Account = MailAccountIconInfoFactory.Create(account.Account, pictures), AccessibleName = name };
            row.AddPrefix(icon.Root);
            var count = WinoSettingsRows.Suffix(row, Gtk.Label.New(account.UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture)));
            count.AddCssClass("heading");
            count.AddCssClass("dim-label");
            // No app icon badge on Linux (owner decision Q10), so the per-account share (ContributesToTaskbar) has nothing to feed.
            WinoSettingsRows.Suffix(row, rows.Button(Translator.UnreadBadges_Account_Configure, ViewModel.ConfigureAccountCommand, () => account));
            _accounts.Add(row);
        }
    }
}
