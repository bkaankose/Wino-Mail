using System.Globalization;
using System.Windows.Input;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// One account's unread badges (Windows AccountUnreadBadgePage, iOS AccountUnreadBadgePageViewController; the Windows
/// taskbar badge is the app icon badge here, with the iOS wording — Android launchers draw it from Wino's
/// notifications): the totals card, Where badges appear (account badge, app icon badge and folder badges, each with a
/// switch and a small illustration under the text), What the account badge counts (Inbox only or Selected folders), the
/// Folders table with Count and Badge check boxes and its two hints, and Restore badge defaults. Preference writes are
/// queued by the ViewModel and drained when the page leaves (the route's deactivation).
/// </summary>
public sealed class AccountUnreadBadgePageFragment : SettingsPageFragment<AccountUnreadBadgePageViewModel>, ISettingsPageTitleSource
{
    private LinearLayout? _folderRows;

    /// <summary>Count and Badge columns: a check box each.</summary>
    private int CheckColumnWidth => WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_check_column);
    private BindingScope? _rowScope;

    public string? PageTitle => ViewModel is { AccountName: { Length: > 0 } name }
        ? string.Format(CultureInfo.CurrentCulture, Translator.UnreadBadges_AccountSettingsTitleFormat, name)
        : Translator.UnreadBadges_Title;

    protected override void BuildPage()
    {
        var vm = VM;
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, _ => UpdateTitle());

        var error = InfoBar(WinoInfoBarSeverity.Error, null, null);
        Bind.Bind(vm, nameof(vm.PreferenceError), s => s.PreferenceError, text =>
        {
            error.Message = text;
            error.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        Add(error);

        Add(TotalsCard());
        Add(LocationsExpander());
        Add(CountSourceExpander());
        Add(FoldersExpander());

        AddGroup(null, ActionCard(Translator.UnreadBadges_RestoreDefaults, vm.RestoreDefaultsCommand));
    }

    /// <summary>Account picture, name and address; the account and app icon totals as two large numbers under them.</summary>
    private WinoSettingsCard TotalsCard()
    {
        var vm = VM;
        var card = Card(string.Empty, null);
        var picture = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_mail_account_icon) };
        card.LeadingView = picture;
        var pictures = MainApplication.Services?.GetService<IPictureStorageService>();
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, (MailAccount? account) =>
        {
            picture.DisplayName = account?.Name;
            picture.Account = account is null || pictures is null ? null : MailAccountIconInfoFactory.Create(account, pictures);
        });
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, name => card.Header = name ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountAddress), s => s.AccountAddress, address => card.Description = address);

        var totals = Row(
            Total(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, Translator.UnreadBadges_AccountBadgeCaption),
            Total(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, Translator.IOS_UnreadBadges_AppIconBadgeCaption));
        if (totals.ChildCount > 1 && totals.GetChildAt(1)?.LayoutParameters is LinearLayout.LayoutParams second)
            second.MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xl);
        card.BottomContent = totals;
        return card;
    }

    private LinearLayout Total(AccountUnreadBadgePageViewModel vm, string property, Func<AccountUnreadBadgePageViewModel, int> read, string caption)
    {
        var number = Text(string.Empty, WinoTextStyle.Display);
        number.Gravity = GravityFlags.CenterHorizontal;
        var label = Text(caption, WinoTextStyle.Caption);
        label.Gravity = GravityFlags.CenterHorizontal;
        var stack = Stack(Resource.Dimension.wino_space_3xs, number, label);
        stack.Focusable = true;
        stack.ImportantForAccessibility = ImportantForAccessibility.Yes;
        Bind.Bind(vm, property, read, value =>
        {
            number.Text = value.ToString(CultureInfo.CurrentCulture);
            stack.ContentDescription = $"{value} {caption}";
        });
        return stack;
    }

    private WinoSettingsExpander LocationsExpander()
    {
        var vm = VM;
        var expander = new WinoSettingsExpander(Ctx, Translator.UnreadBadges_Locations_Title, Translator.UnreadBadges_Locations_Description, WinoIconGlyph.AlertBadge, isExpanded: true);
        expander.Add(LocationRow(Translator.UnreadBadges_AccountBadge_Title, Translator.UnreadBadges_AccountBadge_Description, AccountIllustration(),
            Bind.Switch(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, (s, v) => s.IsAccountBadgeEnabled = v, Translator.UnreadBadges_AccountBadge_Title)));
        expander.Add(LocationRow(Translator.IOS_UnreadBadges_AppIconBadge_Title, Translator.IOS_UnreadBadges_AppIconBadge_Description, AppIconIllustration(),
            Bind.Switch(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, (s, v) => s.IsTaskbarBadgeEnabled = v, Translator.IOS_UnreadBadges_AppIconBadge_Title)));
        expander.Add(LocationRow(Translator.UnreadBadges_FolderBadges_Title, Translator.UnreadBadges_FolderBadges_Description, FolderIllustration(),
            Bind.Switch(vm, nameof(vm.AreFolderBadgesEnabled), s => s.AreFolderBadgesEnabled, (s, v) => s.AreFolderBadgesEnabled = v, Translator.UnreadBadges_FolderBadges_Title)));
        return expander;
    }

    /// <summary>The switch trails the text; the illustration sits under the text, where a phone row has room for it.</summary>
    private WinoSettingsCard LocationRow(string title, string description, View illustration, View control)
    {
        var card = Card(title, description, WinoIconGlyph.None, control);
        var host = new FrameLayout(Ctx);
        host.AddView(illustration, new FrameLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_width), WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_height)));
        card.BottomContent = host;
        return card;
    }

    /// <summary>The illustration-sized tile that frames each illustration.</summary>
    private FrameLayout IllustrationTile()
    {
        var tile = new FrameLayout(Ctx) { ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants };
        var fill = new GradientDrawable();
        fill.SetColor(WinoTokens.Color(Ctx, Resource.Color.wino_mail_illustration_fill));
        fill.SetStroke(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline), WinoTokens.Color(Ctx, Resource.Color.wino_mail_illustration_stroke));
        fill.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_radius_medium));
        tile.Background = fill;
        return tile;
    }

    private TextView CountPill(Color fill, Color text, Color? border = null)
    {
        var pill = new TextView(Ctx) { Gravity = GravityFlags.Center };
        TextViewCompat.SetTextAppearance(pill, Resource.Style.TextAppearance_WinoKit_Mail_IllustrationPill);
        pill.SetTextColor(text);
        pill.SetIncludeFontPadding(false);
        pill.SetMinWidth(WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_pill_height));
        WinoTokens.Pad(pill, Resource.Dimension.wino_space_2xs, 0, Resource.Dimension.wino_space_2xs, 0);
        var background = new GradientDrawable();
        background.SetColor(fill);
        background.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_mail_badge_pill_radius));
        if (border is { } stroke) background.SetStroke(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline), stroke);
        pill.Background = background;
        return pill;
    }

    private TextView Tiny(string? text, bool secondary = false)
    {
        var label = Text(text, WinoTextStyle.Body, maxLines: 1);
        TextViewCompat.SetTextAppearance(label, secondary
            ? Resource.Style.TextAppearance_WinoKit_Mail_IllustrationSecondary
            : Resource.Style.TextAppearance_WinoKit_Mail_Illustration);
        label.SetIncludeFontPadding(false);
        return label;
    }

    /// <summary>The account row in the navigation: colour dot, name and the account badge (hidden while it is off).</summary>
    private View AccountIllustration()
    {
        var vm = VM;
        var tile = IllustrationTile();
        var dot = new View(Ctx);
        var dotFill = new GradientDrawable();
        dotFill.SetShape(ShapeType.Oval);
        dot.Background = dotFill;
        var name = Tiny(string.Empty);
        var pill = CountPill(WinoTokens.Color(Ctx, Resource.Color.wino_mail_illustration_pill), WinoColors.PrimaryText(Ctx));
        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(dot, new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_dot), WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_dot)));
        row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs) });
        row.AddView(pill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_pill_height)) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs) });
        WinoTokens.Pad(row, Resource.Dimension.wino_space_xs, 0, Resource.Dimension.wino_space_xs, 0);
        tile.AddView(row, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, (MailAccount? account) =>
            dotFill.SetColor(WinoColors.FromHexString(account?.AccountColorHex) ?? WinoColors.AvatarColor(account?.Address)));
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, value => name.Text = value ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, value => pill.Text = value.ToString(CultureInfo.CurrentCulture));
        Bind.Bind(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, on => pill.Visibility = on ? ViewStates.Visible : ViewStates.Invisible);
        return tile;
    }

    /// <summary>Wino's launcher icon with the red combined badge (hidden while this account does not contribute).</summary>
    private View AppIconIllustration()
    {
        var vm = VM;
        var tile = IllustrationTile();
        View icon;
        if (AppIcon() is { } drawable)
        {
            var image = new ImageView(Ctx);
            image.SetImageDrawable(drawable);
            icon = image;
        }
        else
        {
            // No launcher icon: an accent tile with the mail glyph stands in.
            var stand = new WinoIconView(Ctx) { Icon = WinoIconGlyph.Mail, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_s) };
            stand.SetTextColor(WinoColors.OnAccent(Ctx));
            var fill = new GradientDrawable();
            fill.SetColor(WinoColors.Accent(Ctx));
            fill.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_radius_medium));
            stand.Background = fill;
            icon = stand;
        }
        int size = WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_icon);
        tile.AddView(icon, new FrameLayout.LayoutParams(size, size, GravityFlags.Center) { RightMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        var badge = CountPill(WinoTokens.Color(Ctx, Resource.Color.wino_badge_fill), WinoTokens.Color(Ctx, Resource.Color.wino_on_badge), WinoTokens.Color(Ctx, Resource.Color.wino_on_badge));
        // Over the icon's top trailing corner.
        tile.AddView(badge, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_pill_height), GravityFlags.Top | GravityFlags.Left)
        {
            LeftMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_badge_illustration_badge_offset),
            TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)
        });
        Bind.Bind(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, value => badge.Text = value.ToString(CultureInfo.CurrentCulture));
        Bind.Bind(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, on => badge.Visibility = on ? ViewStates.Visible : ViewStates.Gone);
        return tile;
    }

    private Drawable? AppIcon()
    {
        try
        {
            return Ctx.ApplicationInfo is { } info ? Ctx.PackageManager?.GetApplicationIcon(info) : null;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return null;
        }
    }

    /// <summary>Two folder lines from the ViewModel's illustration folders, each number shown only while its badge is on.</summary>
    private View FolderIllustration()
    {
        var vm = VM;
        var tile = IllustrationTile();
        var lines = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        lines.SetGravity(GravityFlags.CenterVertical);
        WinoTokens.Pad(lines, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_xs, Resource.Dimension.wino_space_2xs);
        tile.AddView(lines, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        void Rebuild(List<UnreadBadgeFolderViewModel>? folders)
        {
            lines.RemoveAllViews();
            foreach (var folder in (folders ?? []).Take(2))
            {
                var line = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
                var name = Tiny(folder.FolderName);
                var count = Tiny(folder.UnreadCount.ToString(CultureInfo.CurrentCulture), secondary: true);
                count.Visibility = folder.IsIllustrationBadgeVisible ? ViewStates.Visible : ViewStates.Gone;
                line.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
                line.AddView(count, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs) });
                lines.AddView(line, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = lines.ChildCount > 0 ? WinoTokens.Px(Ctx, Resource.Dimension.wino_space_3xs) : 0 });
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
        var vm = VM;
        var expander = new WinoSettingsExpander(Ctx, Translator.UnreadBadges_CountSource_Title, Translator.IOS_UnreadBadges_CountSource_Description, WinoIconGlyph.Folder, isExpanded: true);
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
        var glyph = new WinoIconView(Ctx) { Icon = WinoIconGlyph.Circle, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_l) };
        var card = Card(title, description);
        card.LeadingView = glyph;
        card.IsClickable = true;
        card.ShowsChevron = false;
        EventHandler handler = (_, _) =>
        {
            try { selected(); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return (card, glyph);
    }

    private void SetRadio((WinoSettingsCard Card, WinoIconView Glyph) row, bool on)
    {
        row.Glyph.Icon = on ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle;
        row.Glyph.SetTextColor(on ? WinoColors.Accent(Ctx) : WinoColors.SecondaryText(Ctx));
        row.Card.Selected = on;
    }

    /// <summary>The Folders table: hints, the Count / Badge column header and one row per folder.</summary>
    private WinoSettingsExpander FoldersExpander()
    {
        var vm = VM;
        var expander = new WinoSettingsExpander(Ctx, Translator.UnreadBadges_Folders_Title, Translator.IOS_UnreadBadges_Folders_Description, WinoIconGlyph.Library, null, isExpanded: true);
        // The counted / badged summary joins the header description; a trailing label has no room at phone width.
        Bind.Bind(vm, nameof(vm.FolderSummary), s => s.FolderSummary, summary => expander.HeaderCard.Description = string.IsNullOrEmpty(summary)
            ? Translator.IOS_UnreadBadges_Folders_Description
            : $"{Translator.IOS_UnreadBadges_Folders_Description}\n{summary}");

        var inboxHint = Hint(Translator.UnreadBadges_Folders_InboxOnlyHint, Translator.UnreadBadges_Folders_UseSelectedFolders, vm.UseSelectedFoldersCommand);
        Bind.Visible(inboxHint, vm, nameof(vm.IsInboxOnlySource), s => s.IsInboxOnlySource);
        var badgesHint = Hint(Translator.UnreadBadges_Folders_BadgesOffHint, Translator.UnreadBadges_Folders_TurnOnBadges, vm.TurnOnFolderBadgesCommand);
        Bind.Visible(badgesHint, vm, nameof(vm.IsFolderBadgesOffHintVisible), s => s.IsFolderBadgesOffHintVisible);

        _folderRows = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        var table = Stack(0, inboxHint, badgesHint, ColumnHeader(), _folderRows);
        expander.Add(table, 0, 0, 0, 0);

        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        Bind.Collection(vm.Folders, RebuildFolders);
        return expander;
    }

    /// <summary>An inline accent hint with its fix-it action under the text (the Windows InfoBar-style hint inside the table).</summary>
    private View Hint(string message, string action, ICommand command)
    {
        var icon = new WinoIconView(Ctx) { Icon = WinoIconGlyph.Info, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_s) };
        icon.SetTextColor(WinoColors.Accent(Ctx));
        var text = Text(message, WinoTextStyle.Body);
        var line = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        line.AddView(icon, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        line.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_sm) });
        var button = Bind.Button(action, command);
        var stack = Stack(Resource.Dimension.wino_space_2xs, line, Row(button));
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_2xs);
        stack.SetBackgroundColor(WinoColors.WithAlpha(WinoColors.Accent(Ctx), 0.08));
        return WithSeparator(stack);
    }

    private View ColumnHeader()
    {
        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.AddView(new View(Ctx), new LinearLayout.LayoutParams(0, 1, 1));
        row.AddView(ColumnLabel(Translator.UnreadBadges_Folders_CountColumn), new LinearLayout.LayoutParams(CheckColumnWidth, ViewGroup.LayoutParams.WrapContent));
        row.AddView(ColumnLabel(Translator.UnreadBadges_Folders_BadgeColumn), new LinearLayout.LayoutParams(CheckColumnWidth, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        WinoTokens.Pad(row, 0, Resource.Dimension.wino_space_xs, 0, 0);
        return row;
    }

    private TextView ColumnLabel(string text)
    {
        var label = Text(text, WinoTextStyle.Caption, maxLines: 1);
        label.SetTextColor(WinoColors.TertiaryText(Ctx));
        label.Gravity = GravityFlags.CenterHorizontal;
        return label;
    }

    private void RebuildFolders()
    {
        if (_folderRows is not { } folderRows) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        folderRows.RemoveAllViews();
        foreach (var folder in VM.Folders.ToList())
            folderRows.AddView(FolderRow(folder, rows), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
    }

    /// <summary>Glyph, name and count description, then the Count and Badge check boxes.</summary>
    private View FolderRow(UnreadBadgeFolderViewModel folder, SettingsBinder rows)
    {
        var icon = new WinoIconView(Ctx) { Icon = SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_m) };
        var name = Text(folder.FolderName, WinoTextStyle.Body, maxLines: 1);
        var detail = rows.Label(folder, nameof(folder.CountDescription),
            f => f.IsItemCountFolder ? $"{f.CountDescription} · {Translator.UnreadBadges_Folders_ItemCountNote}" : f.CountDescription,
            maximumLines: 2);
        var text = Stack(Resource.Dimension.wino_space_hairline, name, detail);

        var count = rows.Checkbox(folder, string.Empty, nameof(folder.IsCounted), f => f.IsCounted, (f, v) => f.IsCounted = v);
        rows.Enabled(count, folder, nameof(folder.IsCountedEditable), f => f.IsCountedEditable);
        count.ContentDescription = folder.CountAutomationName;
        var badge = rows.Checkbox(folder, string.Empty, nameof(folder.ShowBadge), f => f.ShowBadge, (f, v) => f.ShowBadge = v);
        rows.Enabled(badge, folder, nameof(folder.IsBadgeEditable), f => f.IsBadgeEditable);
        badge.ContentDescription = folder.BadgeAutomationName;

        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large));
        WinoTokens.Pad(row, 0, Resource.Dimension.wino_space_xs, 0, Resource.Dimension.wino_space_xs);
        row.AddView(icon, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_m) });
        foreach (var box in new View[] { count, badge })
        {
            var cell = new FrameLayout(Ctx);
            cell.AddView(box, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
            row.AddView(cell, new LinearLayout.LayoutParams(CheckColumnWidth, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        }
        return WithSeparator(row);
    }

    /// <summary>The expander's hairline above a row of the hand-built table.</summary>
    private LinearLayout WithSeparator(View row)
    {
        var host = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        var separator = new View(Ctx);
        separator.SetBackgroundColor(WinoColors.CardStroke(Ctx));
        host.AddView(separator, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline)));
        host.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return host;
    }
}
