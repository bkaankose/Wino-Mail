using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Unread badges: per-account contribution to the app badge and launch behaviour (Windows UnreadBadgeSettingsPage).
/// Each account card keeps its switch trailing; the unread count and Configure sit under the text on phone width.
/// </summary>
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

        // Opening the counted folder at launch needs a taskbar or Dock badge (not mobile).
        if (!vm.IsLaunchNavigationAvailable) return;

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
            var description = string.IsNullOrWhiteSpace(account.CountSourceDescription)
                ? account.DescriptionText
                : $"{account.DescriptionText}\n{account.CountSourceDescription}";
            var card = new WinoSettingsCard(account.AccountName ?? string.Empty, description, WinoIconGlyph.Person,
                rows.Switch(account, nameof(account.ContributesToTaskbar), a => a.ContributesToTaskbar, (a, v) => a.ContributesToTaskbar = v, Translator.UnreadBadges_Account_Contribute));

            var count = WinoStyle.Label(string.Format(Translator.UnreadBadges_Account_UnreadFormat, account.UnreadCount), WinoStyle.BodyStrong, WinoStyle.SecondaryText);
            var configure = rows.Button(Translator.UnreadBadges_Account_Configure, ViewModel.ConfigureAccountCommand, () => account);
            var footer = WinoLayout.HStack(WinoStyle.Space3, count, WinoLayout.Spacer(), configure);
            footer.Alignment = UIStackViewAlignment.Center;
            card.BottomContent = footer;
            _accounts.Add(card);
        }
        _accounts.Hidden = _accounts.RowCount == 0;
    }
}
