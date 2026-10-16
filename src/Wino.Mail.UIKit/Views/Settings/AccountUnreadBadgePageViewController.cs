using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// One account's unread badges (Windows AccountUnreadBadgePage; the Windows taskbar badge is the app icon badge here):
/// the totals card, Where badges appear (account badge, app icon badge and folder badges, each with a switch and a small
/// illustration under the text), What the account badge counts (Inbox only or Selected folders), the Folders table with
/// Count and Badge check buttons and its two hints, and Restore badge defaults. Preference writes are queued by the
/// ViewModel and drained when the page leaves.
/// </summary>
public sealed class AccountUnreadBadgePageViewController(AccountUnreadBadgePageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountUnreadBadgePageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    /// <summary>Count and Badge columns: a 44pt check button each.</summary>
    private const double CheckColumnWidth = 56;
    private readonly UIStackView _folderRows = WinoLayout.VStack(0);
    private BindingScope? _rowScope;

    public string? PageTitle => ViewModel.AccountName is { Length: > 0 } name
        ? string.Format(Translator.UnreadBadges_AccountSettingsTitleFormat, name)
        : Translator.UnreadBadges_Title;
    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var error = InfoBar(WinoInfoBarSeverity.Error, null, null);
        Bind.Bind(vm, nameof(vm.PreferenceError), s => s.PreferenceError, text => { error.Message = text; error.Hidden = string.IsNullOrEmpty(text); });
        Add(error);

        Add(TotalsCard());
        Add(LocationsExpander());
        Add(CountSourceExpander());
        Add(FoldersExpander());

        var restore = Bind.Button(Translator.UnreadBadges_RestoreDefaults, vm.RestoreDefaultsCommand, icon: WinoIconGlyph.ArrowReset);
        AddGroup(null, ActionRow(restore));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    /// <summary>Account picture, name and address; the account and app icon totals as two large numbers under them.</summary>
    private WinoSettingsCard TotalsCard()
    {
        var vm = ViewModel;
        var card = new WinoSettingsCard(string.Empty);
        var picture = new WinoAccountIconView(28);
        card.LeadingView = picture;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, (MailAccount? account) =>
            picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures));
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, name => card.Header = name ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountAddress), s => s.AccountAddress, address => card.Description = address);

        var totals = WinoLayout.HStack(WinoStyle.Space6,
            Total(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, Translator.UnreadBadges_AccountBadgeCaption),
            Total(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, Translator.IOS_UnreadBadges_AppIconBadgeCaption));
        card.BottomContent = SettingsCardLayout.Leading(totals);
        return card;
    }

    private UIStackView Total(AccountUnreadBadgePageViewModel vm, string property, Func<AccountUnreadBadgePageViewModel, int> read, string caption)
    {
        var number = Bind.Label(vm, property, read, WinoStyle.Scaled(26, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        var label = WinoStyle.Label(caption, WinoStyle.Caption, WinoStyle.SecondaryText);
        var stack = WinoLayout.VStack(2, number, label);
        stack.Alignment = UIStackViewAlignment.Center;
        stack.IsAccessibilityElement = true;
        Bind.Bind(vm, property, read, value => WinoAccessibility.Label(stack, $"{value} {caption}"));
        return stack;
    }

    private WinoSettingsExpander LocationsExpander()
    {
        var vm = ViewModel;
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_Locations_Title, Translator.UnreadBadges_Locations_Description, WinoIconGlyph.AlertBadge, isExpanded: true);
        expander.Add(LocationRow(Translator.UnreadBadges_AccountBadge_Title, Translator.UnreadBadges_AccountBadge_Description, AccountIllustration(),
            Bind.Switch(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, (s, v) => s.IsAccountBadgeEnabled = v, Translator.UnreadBadges_AccountBadge_Title)));
        expander.Add(LocationRow(Translator.IOS_UnreadBadges_AppIconBadge_Title, Translator.IOS_UnreadBadges_AppIconBadge_Description, AppIconIllustration(),
            Bind.Switch(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, (s, v) => s.IsTaskbarBadgeEnabled = v, Translator.IOS_UnreadBadges_AppIconBadge_Title)));
        expander.Add(LocationRow(Translator.UnreadBadges_FolderBadges_Title, Translator.UnreadBadges_FolderBadges_Description, FolderIllustration(),
            Bind.Switch(vm, nameof(vm.AreFolderBadgesEnabled), s => s.AreFolderBadgesEnabled, (s, v) => s.AreFolderBadgesEnabled = v, Translator.UnreadBadges_FolderBadges_Title)));
        return expander;
    }

    /// <summary>The switch trails the text; the illustration sits under the text, where a phone row has room for it.</summary>
    private static WinoSettingsCard LocationRow(string title, string description, UIView illustration, UIView control)
        => new(title, description, WinoIconGlyph.None, control) { BottomContent = SettingsCardLayout.Leading(illustration) };

    /// <summary>84×34 white tile that frames each illustration.</summary>
    private static WinoSurfaceView IllustrationTile()
    {
        var tile = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.1)),
            StrokeWidth = 1,
            CornerRadius = 6,
            TranslatesAutoresizingMaskIntoConstraints = false,
            IsAccessibilityElement = false,
            AccessibilityElementsHidden = true
        };
        WinoLayout.Size(tile, 84, 34);
        return tile;
    }

    private static UIView CountPill(UILabel text, UIColor fill, UIColor? border = null)
    {
        var pill = new WinoSurfaceView { Fill = fill, Stroke = border, StrokeWidth = border is null ? 0 : 1, CornerRadius = 7, TranslatesAutoresizingMaskIntoConstraints = false };
        text.TextAlignment = UITextAlignment.Center;
        WinoLayout.Fill(text, pill, 0, 4, 0, 4);
        pill.HeightAnchor.ConstraintEqualTo(14).Active = true;
        pill.WidthAnchor.ConstraintGreaterThanOrEqualTo(14).Active = true;
        return pill;
    }

    private static UIFont Tiny => WinoStyle.Fixed(9);
    private static UIFont TinyStrong => WinoStyle.Fixed(9, UIFontWeight.Semibold);

    /// <summary>The account row in the navigation: colour dot, name and the account badge (hidden while it is off).</summary>
    private UIView AccountIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        var dot = new WinoSurfaceView { CornerRadius = 5, Fill = WinoStyle.Accent };
        WinoLayout.Size(dot, 10, 10);
        var name = WinoStyle.Label(string.Empty, Tiny, WinoStyle.PrimaryText);
        name.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);
        var count = WinoStyle.Label(string.Empty, TinyStrong, WinoStyle.PrimaryText);
        var pill = CountPill(count, WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.14)));
        var row = WinoLayout.HStack(5, dot, name, pill);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, tile, 0, 7, 0, 7);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, (MailAccount? account) =>
            dot.Fill = WinoStyle.FromHexString(account?.AccountColorHex) ?? WinoStyle.AvatarColor(account?.Address));
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, value => name.Text = value ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, value => count.Text = value.ToString());
        Bind.Bind(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, on => pill.Hidden = !on);
        return tile;
    }

    /// <summary>Wino's app icon with the red combined badge (hidden while this account does not contribute).</summary>
    private UIView AppIconIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        UIView icon;
        if (AppIconImage() is { } image)
        {
            icon = new UIImageView(image) { ContentMode = UIViewContentMode.ScaleAspectFill, ClipsToBounds = true, TranslatesAutoresizingMaskIntoConstraints = false };
            icon.Layer.CornerRadius = 6;
        }
        else
        {
            // No bundled icon file (previews): an accent tile with the mail glyph stands in.
            var stand = new WinoSurfaceView { Fill = WinoStyle.Accent, CornerRadius = 6, TranslatesAutoresizingMaskIntoConstraints = false };
            var glyph = new WinoIconView(WinoIconGlyph.Mail, 14, UIColor.White);
            WinoLayout.Size(glyph, 14, 14);
            stand.AddSubview(glyph);
            glyph.CenterXAnchor.ConstraintEqualTo(stand.CenterXAnchor).Active = true;
            glyph.CenterYAnchor.ConstraintEqualTo(stand.CenterYAnchor).Active = true;
            icon = stand;
        }
        WinoLayout.Size(icon, 26, 26);
        tile.AddSubview(icon);
        var count = WinoStyle.Label(string.Empty, TinyStrong, UIColor.White);
        var badge = CountPill(count, UIColor.SystemRed, UIColor.White);
        tile.AddSubview(badge);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor, -4),
            icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor, 1),
            badge.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, -10),
            badge.TopAnchor.ConstraintEqualTo(icon.TopAnchor, -4)
        ]);
        Bind.Bind(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, value => count.Text = value.ToString());
        Bind.Bind(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, on => badge.Hidden = !on);
        return tile;
    }

    /// <summary>The primary app icon named by the bundle's CFBundleIcons, or null.</summary>
    private static UIImage? AppIconImage()
    {
        if (NSBundle.MainBundle.InfoDictionary is not { } info) return null;
        if (info["CFBundleIcons"] is not NSDictionary icons) return null;
        if (icons["CFBundlePrimaryIcon"] is not NSDictionary primary) return null;
        if (primary["CFBundleIconFiles"] is not NSArray { Count: > 0 } files) return null;
        var last = files.GetItem<NSString>(files.Count - 1);
        return last is null ? null : UIImage.FromBundle(last.ToString());
    }

    /// <summary>Two folder lines from the ViewModel's illustration folders, each number shown only while its badge is on.</summary>
    private UIView FolderIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        var lines = WinoLayout.VStack(2);
        lines.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(lines, tile, 5, 7, 5, 7);
        void Rebuild(List<UnreadBadgeFolderViewModel>? folders)
        {
            foreach (var view in lines.ArrangedSubviews) { lines.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
            foreach (var folder in (folders ?? []).Take(2))
            {
                var name = WinoStyle.Label(folder.FolderName, Tiny, WinoStyle.PrimaryText);
                name.LineBreakMode = UILineBreakMode.TailTruncation;
                WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);
                var count = WinoStyle.Label(folder.UnreadCount.ToString(), Tiny, WinoStyle.SecondaryText);
                count.Hidden = !folder.IsIllustrationBadgeVisible;
                lines.AddArrangedSubview(WinoLayout.HStack(4, name, count));
            }
        }
        Bind.Bind(vm, nameof(vm.IllustrationFolders), s => s.IllustrationFolders, Rebuild);
        // Badge visibility follows the account switch even when the folder list itself does not change.
        Bind.Bind(vm, nameof(vm.AreFolderBadgesEnabled), s => s.AreFolderBadgesEnabled, _ => Rebuild(vm.IllustrationFolders));
        return tile;
    }

    /// <summary>Inbox only and Selected folders as an exclusive pair of radio rows.</summary>
    private WinoSettingsExpander CountSourceExpander()
    {
        var vm = ViewModel;
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_CountSource_Title, Translator.IOS_UnreadBadges_CountSource_Description, WinoIconGlyph.Folder, isExpanded: true);
        var inbox = RadioRow(Translator.UnreadBadges_CountSource_InboxOnly_Title, Translator.UnreadBadges_CountSource_InboxOnly_Description, () => vm.IsInboxOnlySource = true);
        var selected = RadioRow(Translator.UnreadBadges_CountSource_SelectedFolders_Title, Translator.UnreadBadges_CountSource_SelectedFolders_Description, () => vm.IsInboxOnlySource = false);
        Bind.Bind(vm, nameof(vm.IsInboxOnlySource), s => s.IsInboxOnlySource, inboxOnly =>
        {
            SetRadio(inbox, inboxOnly);
            SetRadio(selected, !inboxOnly);
        });
        expander.Add(inbox.Card);
        expander.Add(selected.Card);
        return expander;
    }

    /// <summary>A nested row led by its radio glyph; tapping anywhere on the row selects it.</summary>
    private (WinoSettingsCard Card, WinoIconView Glyph) RadioRow(string title, string description, Action selected)
    {
        var glyph = new WinoIconView(WinoIconGlyph.Circle, 20, WinoStyle.SecondaryText);
        WinoLayout.Size(glyph, 20, 20);
        var card = new WinoSettingsCard(title, description) { LeadingView = glyph, IsClickable = true, ShowsChevron = false };
        EventHandler handler = (_, _) =>
        {
            try { selected(); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return (card, glyph);
    }

    private static void SetRadio((WinoSettingsCard Card, WinoIconView Glyph) row, bool on)
    {
        row.Glyph.Icon = on ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle;
        row.Glyph.Tint = on ? WinoStyle.Accent : WinoStyle.SecondaryText;
        row.Card.AccessibilityTraits = on ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    /// <summary>The Folders table: hints, the Count / Badge column header and one row per folder.</summary>
    private WinoSettingsExpander FoldersExpander()
    {
        var vm = ViewModel;
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_Folders_Title, Translator.IOS_UnreadBadges_Folders_Description, WinoIconGlyph.Library, null, isExpanded: true);
        // The counted / badged summary joins the header description; a trailing label has no room at phone width.
        Bind.Bind(vm, nameof(vm.FolderSummary), s => s.FolderSummary, summary => expander.HeaderCard.Description = string.IsNullOrEmpty(summary)
            ? Translator.IOS_UnreadBadges_Folders_Description
            : $"{Translator.IOS_UnreadBadges_Folders_Description}\n{summary}");

        var inboxHint = Hint(Translator.UnreadBadges_Folders_InboxOnlyHint, Translator.UnreadBadges_Folders_UseSelectedFolders, vm.UseSelectedFoldersCommand);
        Bind.Visible(inboxHint, vm, nameof(vm.IsInboxOnlySource), s => s.IsInboxOnlySource);
        var badgesHint = Hint(Translator.UnreadBadges_Folders_BadgesOffHint, Translator.UnreadBadges_Folders_TurnOnBadges, vm.TurnOnFolderBadgesCommand);
        Bind.Visible(badgesHint, vm, nameof(vm.IsFolderBadgesOffHintVisible), s => s.IsFolderBadgesOffHintVisible);

        _folderRows.Alignment = UIStackViewAlignment.Fill;
        var table = WinoLayout.VStack(0, inboxHint, badgesHint, ColumnHeader(), _folderRows);
        table.Alignment = UIStackViewAlignment.Fill;
        expander.Add(table, 0, 0, 0, 0);

        Bind.Collection(vm.Folders, RebuildFolders);
        return expander;
    }

    /// <summary>An inline accent hint with its fix-it action under the text, the Windows InfoBar-style hint inside the table.</summary>
    private UIView Hint(string message, string action, System.Windows.Input.ICommand command)
    {
        var surface = new WinoSurfaceView { Fill = SettingsRowParts.AccentWash, TranslatesAutoresizingMaskIntoConstraints = false };
        var icon = new WinoIconView(WinoIconGlyph.Info, 14, WinoStyle.Accent);
        WinoLayout.Size(icon, 14, 14);
        var text = WinoStyle.Label(message, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText, 0);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var line = WinoLayout.HStack(10, icon, text);
        line.Alignment = UIStackViewAlignment.FirstBaseline;
        var button = new SettingsPillButton(action, washed: false);
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var binding = Bindings.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        Bind.OnActivated(button, binding.Execute);
        var stack = WinoLayout.VStack(2, line, SettingsCardLayout.Leading(button));
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, surface, 10, WinoSettingsStyle.CardPadding, 4, WinoSettingsStyle.CardPadding);
        return WithSeparator(surface);
    }

    private static UIView ColumnHeader()
    {
        var count = ColumnLabel(Translator.UnreadBadges_Folders_CountColumn);
        var badge = ColumnLabel(Translator.UnreadBadges_Folders_BadgeColumn);
        var row = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), count, badge);
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 6, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        return host;
    }

    private static UILabel ColumnLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.TertiaryText);
        label.TextAlignment = UITextAlignment.Center;
        label.AdjustsFontSizeToFitWidth = true;
        label.MinimumScaleFactor = 0.8f;
        label.WidthAnchor.ConstraintEqualTo((nfloat)CheckColumnWidth).Active = true;
        return label;
    }

    private void RebuildFolders()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        foreach (var view in _folderRows.ArrangedSubviews) { _folderRows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var folder in ViewModel.Folders.ToList()) _folderRows.AddArrangedSubview(FolderRow(folder, rows));
    }

    /// <summary>Glyph, name and count description, then the Count and Badge check buttons.</summary>
    private static UIView FolderRow(UnreadBadgeFolderViewModel folder, SettingsBinder rows)
    {
        var icon = new WinoIconView(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(folder.FolderName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = UILineBreakMode.TailTruncation;
        var detail = rows.Label(folder, nameof(folder.CountDescription),
            f => f.IsItemCountFolder ? $"{f.CountDescription} · {Translator.UnreadBadges_Folders_ItemCountNote}" : f.CountDescription,
            WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 2);
        foreach (var label in new[] { name, detail }) WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        var text = WinoLayout.VStack(1, name, detail);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);

        var count = rows.Checkbox(folder, string.Empty, nameof(folder.IsCounted), f => f.IsCounted, (f, v) => f.IsCounted = v);
        rows.Enabled(count, folder, nameof(folder.IsCountedEditable), f => f.IsCountedEditable);
        WinoAccessibility.Label(count, folder.CountAutomationName);
        var badge = rows.Checkbox(folder, string.Empty, nameof(folder.ShowBadge), f => f.ShowBadge, (f, v) => f.ShowBadge = v);
        rows.Enabled(badge, folder, nameof(folder.IsBadgeEditable), f => f.IsBadgeEditable);
        WinoAccessibility.Label(badge, folder.BadgeAutomationName);
        foreach (var box in new[] { count, badge })
        {
            box.HorizontalAlignment = UIControlContentHorizontalAlignment.Center;
            WinoLayout.Size(box, CheckColumnWidth, WinoStyle.MinimumHitTarget);
        }

        var row = WinoLayout.HStack(WinoStyle.Space2, icon, text, count, badge);
        row.Alignment = UIStackViewAlignment.Center;
        row.SetCustomSpacing(12, icon);
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 6, WinoSettingsStyle.CardPadding, 6, WinoSettingsStyle.CardPadding);
        host.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return WithSeparator(host);
    }

    /// <summary>Adds the expander's hairline above a row of the hand-built table.</summary>
    private static UIView WithSeparator(UIView row)
    {
        var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
        row.AddSubview(separator);
        NSLayoutConstraint.ActivateConstraints(
        [
            separator.TopAnchor.ConstraintEqualTo(row.TopAnchor),
            separator.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            separator.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor)
        ]);
        return row;
    }
}
