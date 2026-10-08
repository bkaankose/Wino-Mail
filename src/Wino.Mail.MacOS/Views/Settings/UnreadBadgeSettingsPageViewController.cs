using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>Unread badges: per-account contribution to the app badge and launch behaviour (Windows UnreadBadgeSettingsPage).</summary>
public sealed class UnreadBadgeSettingsPageViewController(UnreadBadgeSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<UnreadBadgeSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _accounts = new(Translator.UnreadBadges_Accounts_Title, Translator.UnreadBadges_Accounts_Description);
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Add(_accounts);
        Bind.Collection(vm.Accounts, RebuildAccounts);

        AddGroup(Translator.UnreadBadges_LaunchBehaviour_Title,
            Card(Translator.UnreadBadges_Launch_Title, Translator.UnreadBadges_Launch_Description, WinoIconGlyph.Open,
                Bind.Switch(vm, nameof(vm.IsLaunchNavigationEnabled), s => s.IsLaunchNavigationEnabled, (s, v) => s.IsLaunchNavigationEnabled = v, Translator.UnreadBadges_Launch_Title)));
    }

    private void RebuildAccounts()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _accounts.Clear();
        foreach (var account in ViewModel.Accounts.ToList())
        {
            var count = WinoStyle.Label(account.UnreadCount.ToString(), WinoStyle.BodyStrong, WinoStyle.SecondaryText);
            var card = new WinoSettingsCard(account.AccountName ?? string.Empty,
                string.IsNullOrWhiteSpace(account.CountSourceDescription) ? account.DescriptionText : $"{account.DescriptionText}\n{account.CountSourceDescription}",
                WinoIconGlyph.Person,
                Row(count,
                    rows.Switch(account, nameof(account.ContributesToTaskbar), a => a.ContributesToTaskbar, (a, v) => a.ContributesToTaskbar = v, Translator.UnreadBadges_Account_Contribute),
                    rows.Button(Translator.UnreadBadges_Account_Configure, ViewModel.ConfigureAccountCommand, () => account)));
            _accounts.Add(card);
        }
        _accounts.Hidden = _accounts.RowCount == 0;
    }
}
