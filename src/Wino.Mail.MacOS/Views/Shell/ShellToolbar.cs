using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;
using Wino.Presentation.Controls;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// The unified window toolbar: sidebar toggle and synchronization over the pane, then, past the separator
/// that tracks the pane divider, the title and subtitle, the mail actions in three groups (reply, file,
/// mark) while a mail page is current, then the search field, Daily briefing, What's New and the
/// Wino account at the trailing edge. The Message menu runs the same commands. Every glyph comes
/// from the WinoIcons font.
/// </summary>
internal sealed class ShellToolbar : NSToolbarDelegate
{
    private const string ToggleSidebarId = "wino.toggleSidebar";
    private const string ModesId = "wino.modes";
    private const string SyncId = "wino.sync";
    private const string SidebarSeparatorId = "wino.sidebarSeparator";
    private const string TitleId = "wino.title";
    private const string SearchId = "wino.search";
    private const string BriefingId = "wino.briefing";
    private const string WhatsNewId = "wino.whatsNew";
    private const string AccountId = "wino.account";
    private const string CommandIdPrefix = "wino.command.";

    /// <summary>The mail actions in toolbar order, grouped by the spaces in <see cref="Identifiers"/>.</summary>
    private static readonly (ShellCommand Command, WinoIconGlyph Glyph, Func<string> Title)[] Commands =
    [
        (ShellCommand.Reply, WinoIconGlyph.Reply, () => Translator.MailOperation_Reply),
        (ShellCommand.ReplyAll, WinoIconGlyph.ReplyAll, () => Translator.MailOperation_ReplyAll),
        (ShellCommand.Forward, WinoIconGlyph.Forward, () => Translator.MailOperation_Forward),
        (ShellCommand.Archive, WinoIconGlyph.Archive, () => Translator.MailOperation_Archive),
        (ShellCommand.Delete, WinoIconGlyph.Delete, () => Translator.MailOperation_Delete),
        (ShellCommand.Move, WinoIconGlyph.Move, () => Translator.MailOperation_Move),
        (ShellCommand.Flag, WinoIconGlyph.Flag, () => Translator.MailOperation_Flag),
        (ShellCommand.ToggleRead, WinoIconGlyph.MarkRead, () => Translator.MailOperation_MarkAsRead)
    ];

    private readonly Func<bool> _hasCommandTarget;
    private readonly Func<ShellCommand, bool> _canExecute;
    private readonly Func<ShellCommand, bool> _isActive;
    private readonly Action<ShellCommand, NSView> _execute;
    private readonly Dictionary<ShellCommand, NSToolbarItem> _commandItems = new();
    private bool _navigationalSpaceAdded;
    private NSToolbar? _toolbar;

    /// <summary>The three command groups (reply, file, mark); the reader layout setting orders and hides members.</summary>
    private static readonly ShellCommand[][] CommandGroups =
    [
        [ShellCommand.Reply, ShellCommand.ReplyAll, ShellCommand.Forward],
        [ShellCommand.Archive, ShellCommand.Delete, ShellCommand.Move],
        [ShellCommand.Flag, ShellCommand.ToggleRead]
    ];

    private ShellCommand[][] _visibleGroups = CommandGroups;
    private bool _showsCommandLabels;

    private readonly Func<IShellSearchTarget?> _searchTarget;
    private readonly Action _toggleSidebar;
    private readonly Action _synchronize;
    private readonly Action<NSView> _briefing;
    private readonly Action _whatsNew;
    private readonly Action<NSView> _account;
    private readonly Action<Exception> _error;
    private NSToolbarItem? _syncItem;
    private NSButton? _syncButton;
    private NSProgressIndicator? _syncProgress;
    private WinoSurfaceView? _relayFallbackDot;
    private bool _relayFallbackActive;
    private NSSearchToolbarItem? _searchItem;
    private NSToolbarItem? _briefingItem;
    private NSButton? _briefingButton;
    private WinoSurfaceView? _briefingDot;
    private NSToolbarItem? _whatsNewItem;
    private NSToolbarItem? _accountItem;
    private bool _briefingVisible;
    private bool _briefingUnseen;
    private bool _briefingOpen;
    private bool _whatsNewVisible;
    private bool _accountHidden;

    public ShellToolbar(Func<IShellSearchTarget?> searchTarget, Action toggleSidebar, Action synchronize,
        Action<NSView> briefing, Action whatsNew, Action<NSView> account, Action<Exception> error,
        Func<bool> showsCommands, Func<ShellCommand, bool> canExecute, Func<ShellCommand, bool> isActive, Action<ShellCommand, NSView> execute)
    {
        _hasCommandTarget = showsCommands;
        _canExecute = canExecute;
        _isActive = isActive;
        _execute = execute;
        _searchTarget = searchTarget;
        _toggleSidebar = toggleSidebar;
        _synchronize = synchronize;
        _briefing = briefing;
        _whatsNew = whatsNew;
        _account = account;
        _error = error;
    }

    private readonly NSTextField _title = WinoStyle.Label("Wino Mail", NSFont.BoldSystemFontOfSize(13), WinoStyle.PrimaryText);
    private readonly NSTextField _subtitle = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(11), WinoStyle.SecondaryText);

    /// <summary>The window title and subtitle shown over the content (folder and account in Mail, the mode title elsewhere).</summary>
    public void SetTitle(string title, string subtitle)
    {
        _title.StringValue = title;
        _subtitle.StringValue = subtitle;
        _subtitle.Hidden = string.IsNullOrWhiteSpace(subtitle);
    }

    private NSToolbarItem CreateTitleItem()
    {
        foreach (var label in new[] { _title, _subtitle })
        {
            label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        }
        var stack = WinoLayout.VStack(0, _title, _subtitle);
        stack.DetachesHiddenViews = true;
        stack.EdgeInsets = new NSEdgeInsets(0, 6, 0, 6);
        stack.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;
        var item = new NSToolbarItem(TitleId) { View = stack, Label = string.Empty, PaletteLabel = "Title", Bordered = false };
        // Unbordered: macOS 26 otherwise draws a glass capsule behind the text, unlike the native window title.
        return item;
    }

    /// <summary>The shell split view whose first divider (pane | content) the title separator tracks. Set before <see cref="CreateToolbar"/>.</summary>
    public NSSplitView? SidebarSplitView { get; set; }

    public NSToolbar CreateToolbar()
    {
        var toolbar = new NSToolbar("WinoShellToolbar")
        {
            Delegate = this,
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
            AutosavesConfiguration = false
        };
        _toolbar = toolbar;
        return toolbar;
    }

    private static readonly string[] LeadingIdentifiers =
    [
        ToggleSidebarId,
        // Shown only while the pane is collapsed: the pane's mode switcher is gone with it.
        ModesId,
        // Adjacent buttons share one glass capsule; the space gives sync a capsule of its own.
        NSToolbar.NSToolbarSpaceItemIdentifier,
        SyncId,
        // Follows the pane divider, so the title and subtitle start over the content instead of being
        // squeezed into the pane's width beside the sidebar toggle and sync. AppKit keeps its own window
        // title before the separator, so the shell draws the title as an item and hides the native one.
        SidebarSeparatorId,
        TitleId,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier
    ];

    // No flexible space here: a second one would split the free width and centre the mail actions instead of
    // keeping them against the search field.
    private static readonly string[] TrailingIdentifiers =
    [
        SearchId,
        BriefingId,
        WhatsNewId,
        AccountId
    ];

    private string[] Identifiers() => [.. LeadingIdentifiers, .. CommandIdentifiers(), .. TrailingIdentifiers];

    /// <summary>
    /// NSToolbarItem.Hidden needs macOS 15. On macOS 14 a hidden item is left out of the toolbar instead,
    /// and <see cref="ReconcileLegacyItems"/> removes or re-inserts it.
    /// </summary>
    private bool _legacyCommandsShown;
    private bool _syncHidden;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("macos15.0")]
    private static bool SupportsHiddenItems => OperatingSystem.IsMacOSVersionAtLeast(15);

    private string[] VisibleIdentifiers()
    {
        if (SupportsHiddenItems) return Identifiers();
        string[] commands = _legacyCommandsShown ? CommandIdentifiers() : [];
        return [.. LeadingIdentifiers.Where(id => !LegacyHidden(id)), .. commands, .. TrailingIdentifiers.Where(id => !LegacyHidden(id))];
    }

    private bool LegacyHidden(string id) => id switch
    {
        SyncId => _syncHidden,
        ModesId => !_modesShown,
        BriefingId => !_briefingVisible,
        WhatsNewId => !_whatsNewVisible,
        AccountId => _accountHidden,
        _ => false
    };

    private void SetItemHidden(NSToolbarItem? item, bool hidden)
    {
        if (SupportsHiddenItems)
        {
            if (item is not null) item.Hidden = hidden;
            return;
        }
        ReconcileLegacyItems();
    }

    /// <summary>macOS 14: brings the toolbar's items in line with <see cref="VisibleIdentifiers"/>, keeping the items that stay.</summary>
    private void ReconcileLegacyItems()
    {
        if (_toolbar is null || _reconciling) return;
        _reconciling = true;
        try
        {
            var desired = VisibleIdentifiers();
            int index = 0, next = 0;
            while (next < desired.Length || index < _toolbar.Items.Length)
            {
                var items = _toolbar.Items;
                if (next >= desired.Length) { RemoveToolbarItem(index); continue; }
                if (index >= items.Length) { _toolbar.InsertItem(desired[next++], index++); continue; }
                if (items[index].Identifier == desired[next]) { index++; next++; continue; }
                bool laterPresent = false;
                for (int scan = index + 1; scan < items.Length; scan++)
                    if (items[scan].Identifier == desired[next]) { laterPresent = true; break; }
                if (laterPresent) RemoveToolbarItem(index);
                else _toolbar.InsertItem(desired[next++], index++);
            }
        }
        finally { _reconciling = false; }
    }

    private bool _reconciling;

    private void RemoveToolbarItem(int index)
    {
        if (_toolbar is null) return;
        var id = _toolbar.Items[index].Identifier;
        if (id.StartsWith(CommandIdPrefix, StringComparison.Ordinal))
            foreach (var pair in _commandItems.Where(pair => CommandId(pair.Key) == id).ToList())
            {
                _commandItems.Remove(pair.Key);
                if (pair.Value.View is NSButton button) _stableWidths.Remove(button);
            }
        _toolbar.RemoveItem(index);
    }

    /// <summary>The visible command groups in the configured order, a space between groups.</summary>
    private string[] CommandIdentifiers()
    {
        var ids = new List<string>();
        foreach (var group in _visibleGroups)
        {
            if (group.Length == 0) continue;
            if (ids.Count > 0) ids.Add(NSToolbar.NSToolbarSpaceItemIdentifier);
            ids.AddRange(group.Select(CommandId));
        }
        return ids.ToArray();
    }

    private static string CommandId(ShellCommand command) => CommandIdPrefix + command;

    /// <summary>
    /// Applies the reader action layout (Settings › Reader & Composer › Reader actions): the visible actions in
    /// the configured order, as on the Windows reader command bar. Neighbours from the same family (reply, file,
    /// mark) share a group, so the default order keeps its three groups. Move has no layout entry and follows
    /// Delete (or Archive) wherever they go.
    /// </summary>
    public void SetCommandLayout(IEnumerable<ReaderCommandLayoutEntry> entries)
    {
        var ordered = new List<ShellCommand>();
        foreach (var entry in entries)
            if (entry.IsVisible && ToShellCommand(entry.Action) is { } command && !ordered.Contains(command)) ordered.Add(command);
        int moveAfter = ordered.IndexOf(ShellCommand.Delete) is >= 0 and var delete ? delete : ordered.IndexOf(ShellCommand.Archive);
        ordered.Insert(moveAfter >= 0 ? moveAfter + 1 : ordered.Count, ShellCommand.Move);

        var groups = new List<List<ShellCommand>>();
        foreach (var command in ordered)
        {
            if (groups.Count == 0 || Family(groups[^1][^1]) != Family(command)) groups.Add([]);
            groups[^1].Add(command);
        }
        var visibleGroups = groups.Select(group => group.ToArray()).ToArray();
        if (visibleGroups.Select(group => string.Join(',', group)).SequenceEqual(_visibleGroups.Select(group => string.Join(',', group)))) return;
        _visibleGroups = visibleGroups;
        RebuildCommandItems();
    }

    private static int Family(ShellCommand command) => Array.FindIndex(CommandGroups, group => group.Contains(command));

    private static ShellCommand? ToShellCommand(ReaderCommandAction action) => action switch
    {
        ReaderCommandAction.Reply => ShellCommand.Reply,
        ReaderCommandAction.ReplyAll => ShellCommand.ReplyAll,
        ReaderCommandAction.Forward => ShellCommand.Forward,
        ReaderCommandAction.Archive => ShellCommand.Archive,
        ReaderCommandAction.Delete => ShellCommand.Delete,
        ReaderCommandAction.Flag => ShellCommand.Flag,
        ReaderCommandAction.ReadState => ShellCommand.ToggleRead,
        _ => null
    };

    /// <summary>Replaces the command items between the leading and trailing groups with the current layout.</summary>
    private void RebuildCommandItems()
    {
        if (_toolbar is null) return;
        if (!SupportsHiddenItems)
        {
            for (int index = _toolbar.Items.Length - 1; index >= 0; index--)
                if (_toolbar.Items[index].Identifier.StartsWith(CommandIdPrefix, StringComparison.Ordinal)) RemoveToolbarItem(index);
            ReconcileLegacyItems();
            return;
        }
        var items = _toolbar.Items;
        for (int index = items.Length - 1; index >= LeadingIdentifiers.Length; index--)
        {
            var id = items[index].Identifier;
            if (id.StartsWith(CommandIdPrefix, StringComparison.Ordinal) || id == NSToolbar.NSToolbarSpaceItemIdentifier)
                _toolbar.RemoveItem(index);
        }
        _commandItems.Clear();
        _stableWidths.Clear();
        int insertAt = LeadingIdentifiers.Length;
        foreach (var id in CommandIdentifiers()) _toolbar.InsertItem(id, insertAt++);
    }

    /// <summary>
    /// The space before sync must be navigational too, or AppKit moves it after the window title
    /// and the sync button joins the sidebar toggle's capsule. The later spaces separate the command groups.
    /// </summary>
    public override void WillAddItem(NSNotification notification)
    {
        if (_navigationalSpaceAdded) return;
        if (notification.UserInfo?["item"] is NSToolbarItem { Identifier: var identifier } item && identifier == NSToolbar.NSToolbarSpaceItemIdentifier)
        {
            item.Navigational = true;
            _navigationalSpaceAdded = true;
        }
    }

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => VisibleIdentifiers();
    public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Identifiers();

    public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
    {
        switch (itemIdentifier)
        {
            case ToggleSidebarId:
                var toggle = Button(ToggleSidebarId, WinoIconGlyph.PanelLeft, Translator.MacOSMenu_ToggleSidebar, () => _toggleSidebar());
                toggle.ToolTip = $"{Translator.MacOSMenu_ToggleSidebar} (⌃⌘S)";
                toggle.Navigational = true;
                return toggle;
            case ModesId:
                return _modesItem = CreateModesItem();
            case SyncId:
                return _syncItem = CreateSyncItem();
            case TitleId:
                return CreateTitleItem();
            case SidebarSeparatorId when SidebarSplitView is { } splitView:
                return NSTrackingSeparatorToolbarItem.GetTrackingSeparatorToolbar(SidebarSeparatorId, splitView, 0);
            case SearchId:
                return _searchItem = CreateSearchItem();
            case BriefingId:
                return _briefingItem = CreateBriefingItem();
            case WhatsNewId:
                _whatsNewItem = Button(WhatsNewId, WinoIconGlyph.Announcement, Translator.WhatsNew_ShellButtonTooltip, () => _whatsNew());
                if (SupportsHiddenItems) _whatsNewItem.Hidden = !_whatsNewVisible;
                return _whatsNewItem;
            case var id when id.StartsWith(CommandIdPrefix, StringComparison.Ordinal):
                return CreateCommandItem(id);
            case AccountId:
                _accountItem = Button(AccountId, WinoIconGlyph.Person, Translator.WinoAccount_Titlebar_SignedOutTitle, null);
                var view = (NSButton)_accountItem.View!;
                if ((NSImage.ImageNamed("AppIcon") ?? NSApplication.SharedApplication.ApplicationIconImage) is NSImage logo)
                    view.Image = AccountButtonImage(logo);
                view.Activated += (_, _) => Safe(() => _account(view));
                if (SupportsHiddenItems) _accountItem.Hidden = _accountHidden;
                return _accountItem;
        }
        return new NSToolbarItem(itemIdentifier);
    }

    // The app icon's transparent edges let the toolbar's glass show through, so it sits on an opaque plate. The image
    // draws itself each time, so the plate's dynamic colours follow the light and dark appearance.
    private static NSImage AccountButtonImage(NSImage logo)
        => NSImage.ImageWithSize(new CGSize(24, 24), false, rect =>
        {
            var plate = new CGRect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1);
            var path = NSBezierPath.FromRoundedRect(plate, 6, 6);
            NSColor.ControlBackground.SetFill();
            path.Fill();
            NSColor.Separator.SetStroke();
            path.LineWidth = 0.5f;
            path.Stroke();
            logo.Draw(new CGRect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), CGRect.Empty, NSCompositingOperation.SourceOver, 1);
            return true;
        });

    /// <summary>A bordered toolbar button drawing a Wino glyph (17pt, like the Windows 22px viewbox at Mac density).</summary>
    private NSToolbarItem Button(string id, WinoIconGlyph glyph, string label, Action? action)
    {
        var button = new NSButton
        {
            BezelStyle = NSBezelStyle.Toolbar,
            Image = WinoIcons.Image(glyph, 17, null, label),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetButtonType(NSButtonType.MomentaryPushIn);
        WinoAccessibility.Label(button, label);
        if (action is not null) button.Activated += (_, _) => Safe(action);
        button.WidthAnchor.ConstraintGreaterThanOrEqualTo(34).Active = true;
        var item = new NSToolbarItem(id) { View = button, Label = label, PaletteLabel = label, ToolTip = label };
        return item;
    }

    private NSToolbarItem CreateCommandItem(string id)
    {
        var entry = Commands.First(entry => CommandId(entry.Command) == id);
        var title = entry.Title();
        var item = Button(id, entry.Glyph, title, null);
        var button = (NSButton)item.View!;
        button.Font = NSFont.SystemFontOfSize(NSFont.SystemFontSize);
        button.ImageHugsTitle = true;
        // The stable width of Flag and ToggleRead leaves spare room; leading-aligned, it collects after the title
        // instead of between the glyph and the title.
        button.Alignment = NSTextAlignment.Left;
        button.Activated += (_, _) => Safe(() => _execute(entry.Command, button));
        _commandItems[entry.Command] = item;
        ApplyCommandState(entry.Command, item);
        ApplyCommandLabel(button, item);
        return item;
    }

    private readonly Dictionary<NSButton, NSLayoutConstraint> _stableWidths = new();

    /// <summary>
    /// Flag and ToggleRead swap their title ("Flag" / "Clear flag"); the button keeps the wider title's width,
    /// so the change does not shift every item after it.
    /// </summary>
    private void ApplyStableWidth(NSButton button)
    {
        if (_stableWidths.Remove(button, out var previous)) previous.Active = false;
        if (!_showsCommandLabels) return;
        var command = _commandItems.FirstOrDefault(pair => ReferenceEquals(pair.Value.View, button)).Key;
        if (ToggledAppearance(command, false) is not var (offGlyph, offTitle) || ToggledAppearance(command, true) is not var (onGlyph, onTitle)) return;
        var (title, image) = (button.Title, button.Image);
        nfloat width = 0;
        foreach (var (glyph, text) in new[] { (offGlyph, offTitle), (onGlyph, onTitle) })
        {
            button.Title = text;
            button.Image = WinoIcons.Image(glyph, 17, null, text);
            width = (nfloat)Math.Max(width, button.FittingSize.Width);
        }
        button.Title = title;
        button.Image = image;
        var constraint = button.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Math.Ceiling(width));
        constraint.Active = true;
        _stableWidths[button] = constraint;
    }

    /// <summary>Settings › Reader actions › "Show action labels": the mail action buttons show their title beside the glyph.</summary>
    public void SetCommandLabels(bool visible)
    {
        if (_showsCommandLabels == visible) return;
        _showsCommandLabels = visible;
        foreach (var item in _commandItems.Values)
            if (item.View is NSButton button) ApplyCommandLabel(button, item);
    }

    /// <summary>
    /// Without labels the button carries no title at all: an image-only button that kept its title (and the
    /// wider stable width measured with it) drew "Flag" and "Mark as unread" across their glyphs.
    /// </summary>
    private void ApplyCommandLabel(NSButton button, NSToolbarItem item)
    {
        button.Title = _showsCommandLabels ? item.Label : string.Empty;
        button.ImagePosition = _showsCommandLabels ? NSCellImagePosition.ImageLeading : NSCellImagePosition.ImageOnly;
        ApplyStableWidth(button);
    }

    /// <summary>Re-labels the items whose text was fixed when they were created, after the app language changed.</summary>
    public void ApplyLanguage()
    {
        foreach (var (command, item) in _commandItems)
        {
            var entry = Commands.First(entry => entry.Command == command);
            var (glyph, title) = ToggledAppearance(command, _isActive(command)) ?? (entry.Glyph, entry.Title());
            SetItemText(item, title);
            if (item.View is NSButton button)
            {
                button.Image = WinoIcons.Image(glyph, 17, null, title);
                ApplyCommandLabel(button, item);
            }
        }
        if (_whatsNewItem is not null) SetItemText(_whatsNewItem, Translator.WhatsNew_ShellButtonTooltip);
        if (_briefingItem is not null) SetItemText(_briefingItem, Translator.DailyBriefing_Menu);
        SetRelayFallbackActive(_relayFallbackActive);
    }

    private static void SetItemText(NSToolbarItem item, string title)
    {
        item.Label = title;
        item.PaletteLabel = title;
        item.ToolTip = title;
        if (item.View is not NSButton button) return;
        button.ToolTip = title;
        WinoAccessibility.Label(button, title);
    }

    private void ApplyCommandState(ShellCommand command, NSToolbarItem item)
    {
        if (SupportsHiddenItems) item.Hidden = !_hasCommandTarget();
        if (item.View is not NSButton button) return;
        button.Enabled = _canExecute(command);
        if (ToggledAppearance(command, _isActive(command)) is not var (glyph, title) || item.Label == title) return;
        button.Image = WinoIcons.Image(glyph, 17, null, title);
        if (_showsCommandLabels) button.Title = title;
        button.ToolTip = title;
        WinoAccessibility.Label(button, title);
        item.Label = title;
        item.PaletteLabel = title;
        item.ToolTip = title;
    }

    /// <summary>Flag and ToggleRead show the action they will run: Clear flag on a flagged selection, Mark as unread on a read one.</summary>
    private static (WinoIconGlyph Glyph, string Title)? ToggledAppearance(ShellCommand command, bool active) => command switch
    {
        ShellCommand.Flag => active ? (WinoIconGlyph.ClearFlag, Translator.MailOperation_ClearFlag) : (WinoIconGlyph.Flag, Translator.MailOperation_Flag),
        ShellCommand.ToggleRead => active ? (WinoIconGlyph.MarkUnread, Translator.MailOperation_MarkAsUnread) : (WinoIconGlyph.MarkRead, Translator.MailOperation_MarkAsRead),
        _ => null
    };

    /// <summary>Debug: each command item's label, enabled and hidden state.</summary>
    internal string DumpCommands() => string.Join(" | ", _commandItems.Select(pair =>
        $"{pair.Key}='{pair.Value.Label}' enabled={(pair.Value.View as NSButton)?.Enabled} hidden={(SupportsHiddenItems ? pair.Value.Hidden : !_legacyCommandsShown)}"));

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception exception) { _error(exception); }
    }

    private NSToolbarItem CreateSyncItem()
    {
        var label = Translator.Buttons_Sync;
        var item = Button(SyncId, WinoIconGlyph.Sync, label, () => _synchronize());
        _syncButton = (NSButton)item.View!;
        _syncProgress = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            ControlSize = NSControlSize.Small,
            Indeterminate = true,
            IsDisplayedWhenStopped = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        // Relay health: a red dot on the sync button only while relay-based sync runs on the 5 minute fallback timer.
        _relayFallbackDot = new WinoSurfaceView { CornerRadius = 3, Fill = NSColor.SystemRed, Hidden = !_relayFallbackActive };
        _relayFallbackDot.ToolTip = Translator.Relay_FallbackFlyoutMessage;
        WinoAccessibility.Label(_relayFallbackDot, Translator.Relay_FallbackIndicatorAutomationName);
        WinoLayout.Size(_relayFallbackDot, 6, 6);
        // Spinner and dot sit inside the button, as on the briefing button: a container view kept the
        // toolbar from styling the button, so its hover bezel covered only part of the capsule.
        _syncButton.AddSubview(_syncProgress);
        _syncButton.AddSubview(_relayFallbackDot);
        NSLayoutConstraint.ActivateConstraints(
        [
            _syncProgress.CenterXAnchor.ConstraintEqualTo(_syncButton.CenterXAnchor),
            _syncProgress.CenterYAnchor.ConstraintEqualTo(_syncButton.CenterYAnchor),
            _relayFallbackDot.TrailingAnchor.ConstraintEqualTo(_syncButton.TrailingAnchor, -5),
            _relayFallbackDot.TopAnchor.ConstraintEqualTo(_syncButton.TopAnchor, 4)
        ]);
        // Navigational items sit before the window title, so the title follows the sync button.
        item.Navigational = true;
        return item;
    }

    /// <summary>Shows the relay fallback dot. Silent while live updates flow.</summary>
    public void SetRelayFallbackActive(bool active)
    {
        _relayFallbackActive = active;
        if (_relayFallbackDot is not null) _relayFallbackDot.Hidden = !active;
        if (_syncButton is not null)
            _syncButton.ToolTip = active ? $"{Translator.Relay_FallbackFlyoutTitle}. {Translator.Relay_FallbackFlyoutMessage}" : Translator.Buttons_Sync;
    }

    private NSToolbarItem CreateBriefingItem()
    {
        var item = Button(BriefingId, WinoIconGlyph.DailyBriefing, Translator.DailyBriefing_Menu, null);
        _briefingButton = (NSButton)item.View!;
        _briefingButton.SetButtonType(NSButtonType.PushOnPushOff);
        _briefingButton.Activated += (_, _) => Safe(() => _briefing(_briefingButton));
        // The dot sits inside the button itself: wrapping the button in a container stops the
        // toolbar from styling it like its siblings, and the bezel then fills the wrong shape.
        _briefingDot = new WinoSurfaceView { CornerRadius = 3, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_briefingDot, 6, 6);
        _briefingButton.AddSubview(_briefingDot);
        NSLayoutConstraint.ActivateConstraints(
        [
            _briefingDot.TrailingAnchor.ConstraintEqualTo(_briefingButton.TrailingAnchor, -5),
            _briefingDot.TopAnchor.ConstraintEqualTo(_briefingButton.TopAnchor, 4)
        ]);
        if (SupportsHiddenItems) item.Hidden = !_briefingVisible;
        ApplyBriefing();
        return item;
    }

    private NSSearchToolbarItem CreateSearchItem()
    {
        var item = new NSSearchToolbarItem(SearchId)
        {
            Label = Translator.SearchBarPlaceholder,
            PaletteLabel = Translator.SearchBarPlaceholder,
            PreferredWidthForSearchField = 240,
            ResignsFirstResponderWithCancel = true
        };
        var field = item.SearchField;
        field.PlaceholderString = Translator.SearchBarPlaceholder;
        field.SendsWholeSearchString = true;
        field.SendsSearchStringImmediately = false;
        field.MaximumRecents = 10;
        field.RecentsAutosaveName = "WinoMailSearchRecents";
        field.Changed += async (_, _) =>
        {
            try
            {
                if (_searchTarget() is not { } target) return;
                (target as IShellSearchSuggestionTarget)?.SearchFieldEditing(field);
                await target.SearchTextChangedAsync(field.StringValue ?? string.Empty);
            }
            catch (Exception exception) { _error(exception); }
        };
        // Pages with a suggestion list take the arrow keys, Return and Esc while it is open.
        field.DoCommandBySelector = (_, _, selector) =>
        {
            try { return _searchTarget() is IShellSearchSuggestionTarget suggestions && suggestions.SearchFieldCommand(field, selector.Name); }
            catch (Exception exception) { _error(exception); return false; }
        };
        field.EditingEnded += (_, _) =>
        {
            try { (_searchTarget() as IShellSearchSuggestionTarget)?.SearchFieldEndedEditing(field); }
            catch (Exception exception) { _error(exception); }
        };
        field.Activated += async (_, _) =>
        {
            try
            {
                if (_searchTarget() is not { } target) return;
                var text = field.StringValue ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) await target.SearchClearedAsync();
                else await target.SearchSubmittedAsync(text);
            }
            catch (Exception exception) { _error(exception); }
        };
        return item;
    }

    public void FocusSearch() => _searchItem?.BeginSearchInteraction();

    /// <summary>Clears the field without notifying, for example after a folder change.</summary>
    public void ResetSearch() => SetSearchText(string.Empty);

    /// <summary>Sets the field text without notifying, for example the keywords applied from the search filters.</summary>
    public void SetSearchText(string text)
    {
        if (_searchItem?.SearchField is { } field) field.StringValue = text ?? string.Empty;
    }

    public void ShowSearchText(string text)
    {
        if (_searchItem?.SearchField is { CurrentEditor: null } field && field.StringValue != text) field.StringValue = text;
    }

    /// <summary>Re-reads search support from the current content page.</summary>
    public void Revalidate()
    {
        if (!SupportsHiddenItems && _legacyCommandsShown != _hasCommandTarget())
        {
            _legacyCommandsShown = !_legacyCommandsShown;
            ReconcileLegacyItems();
        }
        foreach (var (command, item) in _commandItems) ApplyCommandState(command, item);
        if (_searchItem?.SearchField is not { } field) return;
        var target = _searchTarget();
        field.Enabled = target is not null;
        field.PlaceholderString = target?.SearchPlaceholder ?? Translator.SearchBarPlaceholder;
    }

    /// <summary>Applies the active provider's synchronization state to the toolbar button.</summary>
    public void SetSynchronization(bool supported, bool canSynchronize, bool isSynchronizing, string? toolTip)
    {
        if (_syncItem is null || _syncButton is null || _syncProgress is null) return;
        _syncHidden = !supported;
        SetItemHidden(_syncItem, !supported);
        _syncButton.Enabled = canSynchronize && !isSynchronizing;
        _syncButton.Image = isSynchronizing ? null : WinoIcons.Image(WinoIconGlyph.Sync, 17, null, Translator.Buttons_Sync);
        if (isSynchronizing) _syncProgress.StartAnimation(null); else _syncProgress.StopAnimation(null);
        var tip = string.IsNullOrWhiteSpace(toolTip) ? Translator.Buttons_Sync : toolTip;
        _syncItem.ToolTip = tip;
        _syncButton.ToolTip = tip;
        WinoAccessibility.Label(_syncButton, tip);
    }

    public void SetBriefing(bool visible, bool hasUnseen, bool isOpen)
    {
        _briefingVisible = visible;
        _briefingUnseen = hasUnseen;
        _briefingOpen = isOpen;
        SetItemHidden(_briefingItem, !visible);
        ApplyBriefing();
    }

    private void ApplyBriefing()
    {
        if (_briefingButton is null || _briefingDot is null) return;
        _briefingButton.State = _briefingOpen ? NSCellStateValue.On : NSCellStateValue.Off;
        _briefingDot.Hidden = !_briefingUnseen;
        _briefingDot.Fill = WinoStyle.Accent;
    }

    public void SetWhatsNewVisible(bool visible)
    {
        _whatsNewVisible = visible;
        SetItemHidden(_whatsNewItem, !visible);
    }

    public void SetAccountHidden(bool hidden)
    {
        _accountHidden = hidden;
        SetItemHidden(_accountItem, hidden);
    }

    private NSMenuToolbarItem? _modesItem;
    private bool _modesShown;
    private WinoApplicationMode _currentMode = WinoApplicationMode.Mail;
    private IReadOnlyCollection<WinoApplicationMode>? _visibleModes;

    /// <summary>Runs when a mode is picked from the toolbar's mode menu.</summary>
    public Action<WinoApplicationMode>? ModeSelected { get; set; }

    private NSMenuToolbarItem CreateModesItem()
    {
        var item = new NSMenuToolbarItem(ModesId)
        {
            Label = Translator.KeyboardShortcuts_Mode,
            PaletteLabel = Translator.KeyboardShortcuts_Mode,
            ToolTip = Translator.KeyboardShortcuts_Mode,
            ShowsIndicator = true,
            Navigational = true,
            Menu = new NSMenu { AutoEnablesItems = false }
        };
        if (SupportsHiddenItems) item.Hidden = !_modesShown;
        _modesItem = item;
        ApplyModes();
        return item;
    }

    /// <summary>
    /// The mode menu that replaces the pane's switcher while the pane is collapsed: the current mode's glyph
    /// with a menu of the visible modes and Settings, in the switcher's order.
    /// </summary>
    public void SetModes(WinoApplicationMode current, IReadOnlyCollection<WinoApplicationMode>? visible, bool shown)
    {
        bool changed = current != _currentMode || !ReferenceEquals(visible, _visibleModes);
        _currentMode = current;
        _visibleModes = visible;
        if (_modesShown != shown)
        {
            _modesShown = shown;
            SetItemHidden(_modesItem, !shown);
        }
        if (changed) ApplyModes();
    }

    private void ApplyModes()
    {
        if (_modesItem?.Menu is not { } menu) return;
        var previous = menu.Items;
        menu.RemoveAllItems();
        foreach (var old in previous) old.Dispose();
        // The item pulls its menu down: the first entry is its title and never shows.
        menu.AddItem(new NSMenuItem(Translator.KeyboardShortcuts_Mode));
        foreach (var segment in WinoModeSegments.All)
        {
            bool isSettings = segment.Mode == WinoApplicationMode.Settings;
            if (!isSettings && _visibleModes is { } visible && !visible.Contains(segment.Mode)) continue;
            if (isSettings) menu.AddItem(NSMenuItem.SeparatorItem);
            var mode = segment.Mode;
            var entry = new NSMenuItem(segment.Title, (_, _) => Safe(() => ModeSelected?.Invoke(mode)))
            {
                Image = WinoIcons.Image(segment.Regular, 15, null, segment.Title),
                State = mode == _currentMode ? NSCellStateValue.On : NSCellStateValue.Off
            };
            menu.AddItem(entry);
            if (mode == _currentMode)
            {
                _modesItem.Image = WinoIcons.Image(segment.Regular, 17, null, $"{Translator.KeyboardShortcuts_Mode}, {segment.Title}");
            }
        }
    }
}
