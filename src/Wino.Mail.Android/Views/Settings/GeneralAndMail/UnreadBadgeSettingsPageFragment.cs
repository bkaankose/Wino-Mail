using System.Globalization;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Unread badges: per-account contribution to the combined badge and launch behaviour (Windows UnreadBadgeSettingsPage,
/// iOS UnreadBadgeSettingsPageViewController, same strings). Each account card keeps its switch trailing; the unread
/// count and Configure sit under the text on phone width.
/// </summary>
public sealed class UnreadBadgeSettingsPageFragment : SettingsPageFragment<UnreadBadgeSettingsPageViewModel>
{
    private WinoSettingsGroup? _accounts;
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = VM;
        _accounts = Add(new WinoSettingsGroup(Ctx, Translator.UnreadBadges_Accounts_Title, Translator.UnreadBadges_Accounts_Description));
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        Bind.Collection(vm.Accounts, RebuildAccounts);

        // Opening the counted folder at launch needs a taskbar or Dock badge (not mobile).
        if (!vm.IsLaunchNavigationAvailable) return;

        AddGroup(Translator.UnreadBadges_LaunchBehaviour_Title,
            Card(Translator.UnreadBadges_Launch_Title, Translator.UnreadBadges_Launch_Description, WinoIconGlyph.Open,
                Bind.Switch(vm, nameof(vm.IsLaunchNavigationEnabled), s => s.IsLaunchNavigationEnabled, (s, v) => s.IsLaunchNavigationEnabled = v, Translator.UnreadBadges_Launch_Title)));
    }

    private void RebuildAccounts()
    {
        if (_accounts is not { } accounts) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        accounts.Clear();
        foreach (var account in VM.Accounts.ToList())
        {
            var description = string.IsNullOrWhiteSpace(account.CountSourceDescription)
                ? account.DescriptionText
                : $"{account.DescriptionText}\n{account.CountSourceDescription}";
            var card = new WinoSettingsCard(Ctx, account.AccountName ?? string.Empty, description, WinoIconGlyph.Person,
                rows.Switch(account, nameof(account.ContributesToTaskbar), a => a.ContributesToTaskbar, (a, v) => a.ContributesToTaskbar = v, Translator.UnreadBadges_Account_Contribute));

            var count = Text(string.Format(CultureInfo.CurrentCulture, Translator.UnreadBadges_Account_UnreadFormat, account.UnreadCount), WinoTextStyle.BodyStrong);
            count.SetTextColor(WinoColors.SecondaryText(Ctx));
            var configure = rows.Button(Translator.UnreadBadges_Account_Configure, VM.ConfigureAccountCommand, () => account);
            var footer = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
            footer.SetGravity(GravityFlags.CenterVertical);
            footer.AddView(count, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            footer.AddView(configure, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            footer.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            card.BottomContent = footer;
            accounts.Add(card);
        }
        accounts.Visibility = accounts.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }
}
