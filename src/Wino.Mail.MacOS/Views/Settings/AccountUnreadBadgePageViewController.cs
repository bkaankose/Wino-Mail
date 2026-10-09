using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// One account's unread badges (Windows AccountUnreadBadgePage, "Dock" in place of "taskbar"): the
/// totals card, Where badges appear (account badge, Dock badge and folder badges, each with a small
/// illustration and a switch), What the account badge counts (Inbox only or Selected folders), the
/// Folders table with Count and Badge checkboxes and its two hints, and Restore badge defaults.
/// Preference writes are queued by the ViewModel and drained when the page leaves.
/// </summary>
public sealed class AccountUnreadBadgePageViewController(AccountUnreadBadgePageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountUnreadBadgePageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const double CheckColumnWidth = 70;
    private readonly NSStackView _folderRows = WinoLayout.VStack(0);
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
        var restoreRow = Row(restore, WinoLayout.Spacer());
        restoreRow.EdgeInsets = new NSEdgeInsets(10, 0, 0, 0);
        Add(restoreRow);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    /// <summary>Account identity on the left; the account and Dock totals as two large numbers on the right.</summary>
    private WinoSettingsCard TotalsCard()
    {
        var vm = ViewModel;
        var card = new WinoSettingsCard(string.Empty, null, WinoIconGlyph.None, WinoLayout.HStack(WinoStyle.Space6,
            Total(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, Translator.UnreadBadges_AccountBadgeCaption),
            Total(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, Translator.UnreadBadges_DockBadgeCaption)));
        var picture = new WinoAccountIconView(28);
        card.LeadingView = picture;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
            picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures));
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, name => card.Header = name ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountAddress), s => s.AccountAddress, address => card.Description = address);
        return card;
    }

    private NSStackView Total(AccountUnreadBadgePageViewModel vm, string property, Func<AccountUnreadBadgePageViewModel, int> read, string caption)
    {
        var number = Bind.Label(vm, property, read, NSFont.SystemFontOfSize(26, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        var label = WinoStyle.Label(caption, WinoStyle.Caption, WinoStyle.SecondaryText);
        var stack = WinoLayout.VStack(2, number, label);
        stack.Alignment = NSLayoutAttribute.CenterX;
        Bind.Bind(vm, property, read, value => WinoAccessibility.Label(stack, $"{value} {caption}"));
        stack.AccessibilityElement = true;
        return stack;
    }

    private WinoSettingsExpander LocationsExpander()
    {
        var vm = ViewModel;
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_Locations_Title, Translator.UnreadBadges_Locations_Description, WinoIconGlyph.AlertBadge, isExpanded: true);
        expander.Add(LocationRow(Translator.UnreadBadges_AccountBadge_Title, Translator.UnreadBadges_AccountBadge_Description, AccountIllustration(),
            Bind.Switch(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, (s, v) => s.IsAccountBadgeEnabled = v, Translator.UnreadBadges_AccountBadge_Title)));
        expander.Add(LocationRow(Translator.UnreadBadges_DockBadge_Title, Translator.UnreadBadges_DockBadge_Description, DockIllustration(),
            Bind.Switch(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, (s, v) => s.IsTaskbarBadgeEnabled = v, Translator.UnreadBadges_DockBadge_Title)));
        expander.Add(LocationRow(Translator.UnreadBadges_FolderBadges_Title, Translator.UnreadBadges_FolderBadges_Description, FolderIllustration(),
            Bind.Switch(vm, nameof(vm.AreFolderBadgesEnabled), s => s.AreFolderBadgesEnabled, (s, v) => s.AreFolderBadgesEnabled = v, Translator.UnreadBadges_FolderBadges_Title)));
        return expander;
    }

    private static WinoSettingsCard LocationRow(string title, string description, NSView illustration, NSView control)
        => new(title, description, WinoIconGlyph.None, control) { LeadingView = Indented(illustration) };

    /// <summary>
    /// A nested row's leading view starts at the card padding; this moves it to the header text column
    /// like the other nested rows.
    /// </summary>
    private static NSView Indented(NSView view)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, host, 0, WinoSettingsStyle.NestedIndent - WinoSettingsStyle.CardPadding, 0, 0);
        return host;
    }

    /// <summary>84×34 white tile that frames each illustration.</summary>
    private static WinoSurfaceView IllustrationTile()
    {
        var tile = new WinoSurfaceView
        {
            Fill = SettingsRowParts.RowFill,
            Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.1)),
            CornerRadius = 6,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(tile, 84, 34);
        tile.AccessibilityElement = false;
        return tile;
    }

    private static NSView CountPill(NSTextField text, NSColor fill, NSColor? border = null)
    {
        var pill = new WinoSurfaceView { Fill = fill, Stroke = border, StrokeWidth = border is null ? 0 : 1, CornerRadius = 7, TranslatesAutoresizingMaskIntoConstraints = false };
        text.Alignment = NSTextAlignment.Center;
        WinoLayout.Fill(text, pill, 0, 4, 0, 4);
        pill.HeightAnchor.ConstraintEqualTo(14).Active = true;
        pill.WidthAnchor.ConstraintGreaterThanOrEqualTo(14).Active = true;
        text.CenterYAnchor.ConstraintEqualTo(pill.CenterYAnchor).Active = true;
        return pill;
    }

    private static NSFont Tiny => NSFont.SystemFontOfSize(9);
    private static NSFont TinyStrong => NSFont.SystemFontOfSize(9, NSFontWeight.Semibold);

    /// <summary>The account row in the sidebar: colour dot, name and the account badge (hidden while it is off).</summary>
    private NSView AccountIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        var dot = new WinoSurfaceView { CornerRadius = 5, Fill = WinoStyle.Accent };
        WinoLayout.Size(dot, 10, 10);
        var name = WinoStyle.Label(string.Empty, Tiny, WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var count = WinoStyle.Label(string.Empty, TinyStrong, WinoStyle.PrimaryText);
        var pill = CountPill(count, WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.14)));
        var row = WinoLayout.HStack(5, dot, name, pill);
        row.Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Fill(row, tile, 0, 7, 0, 7);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, (MailAccount? account) =>
            dot.Fill = WinoStyle.FromHexString(account?.AccountColorHex) ?? WinoStyle.AvatarColor(account?.Address));
        Bind.Bind(vm, nameof(vm.AccountName), s => s.AccountName, value => name.StringValue = value ?? string.Empty);
        Bind.Bind(vm, nameof(vm.AccountUnreadCount), s => s.AccountUnreadCount, value => count.StringValue = value.ToString());
        Bind.Bind(vm, nameof(vm.IsAccountBadgeEnabled), s => s.IsAccountBadgeEnabled, on => pill.Hidden = !on);
        return tile;
    }

    /// <summary>Wino's Dock icon with the red combined badge (hidden while this account does not contribute).</summary>
    private NSView DockIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        var icon = new NSImageView
        {
            Image = NSApplication.SharedApplication.ApplicationIconImage,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(icon, 26, 26);
        tile.AddSubview(icon);
        var count = WinoStyle.Label(string.Empty, TinyStrong, NSColor.White);
        var badge = CountPill(count, NSColor.SystemRed, NSColor.White);
        tile.AddSubview(badge);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor, -4),
            icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor, 1),
            badge.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, -10),
            badge.TopAnchor.ConstraintEqualTo(icon.TopAnchor, -4)
        ]);
        Bind.Bind(vm, nameof(vm.TaskbarUnreadCount), s => s.TaskbarUnreadCount, value => count.StringValue = value.ToString());
        Bind.Bind(vm, nameof(vm.IsTaskbarBadgeEnabled), s => s.IsTaskbarBadgeEnabled, on => badge.Hidden = !on);
        return tile;
    }

    /// <summary>Two folder lines from the ViewModel's illustration folders, each number shown only while its badge is on.</summary>
    private NSView FolderIllustration()
    {
        var vm = ViewModel;
        var tile = IllustrationTile();
        var lines = WinoLayout.VStack(2);
        lines.Alignment = NSLayoutAttribute.Leading;
        WinoLayout.Fill(lines, tile, 5, 7, 5, 7);
        void Rebuild(List<UnreadBadgeFolderViewModel>? folders)
        {
            foreach (var view in lines.ArrangedSubviews) { lines.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
            foreach (var folder in (folders ?? []).Take(2))
            {
                var name = WinoStyle.Label(folder.FolderName, Tiny, WinoStyle.PrimaryText);
                name.LineBreakMode = NSLineBreakMode.TruncatingTail;
                name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
                name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
                var count = WinoStyle.Label(folder.UnreadCount.ToString(), Tiny, WinoStyle.SecondaryText);
                count.Hidden = !folder.IsIllustrationBadgeVisible;
                var line = WinoLayout.HStack(4, name, count);
                lines.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(lines.WidthAnchor).Active = true;
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
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_CountSource_Title, Translator.UnreadBadges_CountSource_DockDescription, WinoIconGlyph.Folder, isExpanded: true);
        var inbox = Radio(Translator.UnreadBadges_CountSource_InboxOnly_Title, () => vm.IsInboxOnlySource = true);
        var selected = Radio(Translator.UnreadBadges_CountSource_SelectedFolders_Title, () => vm.IsInboxOnlySource = false);
        Bind.Bind(vm, nameof(vm.IsInboxOnlySource), s => s.IsInboxOnlySource, inboxOnly =>
        {
            inbox.State = inboxOnly ? NSCellStateValue.On : NSCellStateValue.Off;
            selected.State = inboxOnly ? NSCellStateValue.Off : NSCellStateValue.On;
        });
        expander.Add(RadioRow(Translator.UnreadBadges_CountSource_InboxOnly_Title, Translator.UnreadBadges_CountSource_InboxOnly_Description, inbox, () => vm.IsInboxOnlySource = true));
        expander.Add(RadioRow(Translator.UnreadBadges_CountSource_SelectedFolders_Title, Translator.UnreadBadges_CountSource_SelectedFolders_Description, selected, () => vm.IsInboxOnlySource = false));
        return expander;
    }

    private NSButton Radio(string label, Action selected)
    {
        var radio = new NSButton { Title = string.Empty, TranslatesAutoresizingMaskIntoConstraints = false };
        radio.SetButtonType(NSButtonType.Radio);
        WinoAccessibility.Label(radio, label);
        Bind.OnActivated(radio, selected);
        return radio;
    }

    /// <summary>A nested row led by its radio button; clicking anywhere on the row selects it.</summary>
    private WinoSettingsCard RadioRow(string title, string description, NSButton radio, Action selected)
    {
        var card = new WinoSettingsCard(title, description) { LeadingView = Indented(radio), IsClickable = true, ShowsChevron = false };
        EventHandler handler = (_, _) =>
        {
            try { selected(); }
            catch (Exception exception) { ReportError(exception); }
        };
        card.Activated += handler;
        Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        return card;
    }

    /// <summary>The Folders table: hints, the Count / Badge column header and one row per folder.</summary>
    private WinoSettingsExpander FoldersExpander()
    {
        var vm = ViewModel;
        var summary = Bind.Label(vm, nameof(vm.FolderSummary), s => s.FolderSummary, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        var expander = new WinoSettingsExpander(Translator.UnreadBadges_Folders_Title, Translator.UnreadBadges_Folders_DockDescription, WinoIconGlyph.Library, summary, isExpanded: true);

        var inboxHint = Hint(Translator.UnreadBadges_Folders_InboxOnlyHint, Translator.UnreadBadges_Folders_UseSelectedFolders, vm.UseSelectedFoldersCommand);
        Bind.Visible(inboxHint, vm, nameof(vm.IsInboxOnlySource), s => s.IsInboxOnlySource);
        var badgesHint = Hint(Translator.UnreadBadges_Folders_BadgesOffHint, Translator.UnreadBadges_Folders_TurnOnBadges, vm.TurnOnFolderBadgesCommand);
        Bind.Visible(badgesHint, vm, nameof(vm.IsFolderBadgesOffHintVisible), s => s.IsFolderBadgesOffHintVisible);

        var table = WinoLayout.VStack(0, inboxHint, badgesHint, ColumnHeader(), _folderRows);
        table.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in table.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(table.WidthAnchor).Active = true;
        _folderRows.Alignment = NSLayoutAttribute.Leading;
        expander.Add(table, 0, 0, 0, 0);

        Bind.Collection(vm.Folders, RebuildFolders);
        return expander;
    }

    /// <summary>An inline accent hint row with its fix-it action, the Windows InfoBar-style hint inside the table.</summary>
    private NSView Hint(string message, string action, System.Windows.Input.ICommand command)
    {
        var surface = new WinoSurfaceView { Fill = SettingsRowParts.AccentWash, TranslatesAutoresizingMaskIntoConstraints = false };
        var icon = new WinoIconView(WinoIconGlyph.Info, 14, WinoStyle.Accent);
        WinoLayout.Size(icon, 14, 14);
        var text = WinoStyle.Label(message, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText, 0);
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var button = new SettingsPillButton(action, washed: false);
        var binding = Bindings.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        Bind.OnActivated(button, binding.Execute);
        var row = WinoLayout.HStack(10, icon, text, button);
        row.Alignment = NSLayoutAttribute.CenterY;
        WinoLayout.Fill(row, surface, 10, WinoSettingsStyle.CardPadding, 10, WinoSettingsStyle.CardPadding);
        return WithSeparator(surface);
    }

    private static NSView ColumnHeader()
    {
        var spacer = WinoLayout.Spacer();
        var count = ColumnLabel(Translator.UnreadBadges_Folders_CountColumn);
        var badge = ColumnLabel(Translator.UnreadBadges_Folders_BadgeColumn);
        var row = WinoLayout.HStack(WinoStyle.Space4, spacer, count, badge);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 6, WinoSettingsStyle.NestedIndent, 0, WinoSettingsStyle.CardPadding);
        return host;
    }

    private static NSTextField ColumnLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.TertiaryText);
        label.WidthAnchor.ConstraintEqualTo((nfloat)CheckColumnWidth).Active = true;
        return label;
    }

    private void RebuildFolders()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        foreach (var view in _folderRows.ArrangedSubviews) { _folderRows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var folder in ViewModel.Folders.ToList())
        {
            var row = FolderRow(folder, rows);
            _folderRows.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_folderRows.WidthAnchor).Active = true;
        }
    }

    /// <summary>Glyph, name and count description, then the Count and Badge checkboxes.</summary>
    private NSView FolderRow(UnreadBadgeFolderViewModel folder, SettingsBinder rows)
    {
        var glyph = ShellPaneRows.FolderGlyph(folder.SpecialFolderType);
        var icon = new WinoIconView(glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph, 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(folder.FolderName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var detail = rows.Label(folder, nameof(folder.CountDescription),
            f => f.IsItemCountFolder ? $"{f.CountDescription} · {Translator.UnreadBadges_Folders_ItemCountNote}" : f.CountDescription,
            WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
        foreach (var label in new[] { name, detail })
        {
            label.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        }
        var text = WinoLayout.VStack(1, name, detail);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetClippingResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var count = rows.Checkbox(folder, Translator.UnreadBadges_Folders_CountColumn, nameof(folder.IsCounted), f => f.IsCounted, (f, v) => f.IsCounted = v);
        rows.Enabled(count, folder, nameof(folder.IsCountedEditable), f => f.IsCountedEditable);
        WinoAccessibility.Label(count, folder.CountAutomationName);
        var badge = rows.Checkbox(folder, Translator.UnreadBadges_Folders_BadgeColumn, nameof(folder.ShowBadge), f => f.ShowBadge, (f, v) => f.ShowBadge = v);
        rows.Enabled(badge, folder, nameof(folder.IsBadgeEditable), f => f.IsBadgeEditable);
        WinoAccessibility.Label(badge, folder.BadgeAutomationName);
        foreach (var box in new[] { count, badge })
        {
            box.Font = WinoSettingsStyle.CardDescription;
            box.WidthAnchor.ConstraintEqualTo((nfloat)CheckColumnWidth).Active = true;
        }

        var row = WinoLayout.HStack(WinoStyle.Space4, icon, text, count, badge);
        row.Alignment = NSLayoutAttribute.CenterY;
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 8, WinoSettingsStyle.NestedIndent, 8, WinoSettingsStyle.CardPadding);
        host.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        return WithSeparator(host);
    }

    /// <summary>Adds the expander's hairline above a row of the hand-built table.</summary>
    private static NSView WithSeparator(NSView row)
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
