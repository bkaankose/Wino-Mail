using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// The unified window toolbar: sidebar toggle, synchronization (navigational, so it precedes the
/// title), the window title and subtitle (native), the mail actions in three groups (reply, file,
/// mark) while a mail page is current, then the search field, Daily briefing, What's New and the
/// Wino account at the trailing edge. The Message menu runs the same commands. Every glyph comes
/// from the WinoIcons font.
/// </summary>
internal sealed class ShellToolbar : NSToolbarDelegate
{
    private const string ToggleSidebarId = "wino.toggleSidebar";
    private const string SyncId = "wino.sync";
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
        Func<bool> hasCommandTarget, Func<ShellCommand, bool> canExecute, Action<ShellCommand, NSView> execute)
    {
        _hasCommandTarget = hasCommandTarget;
        _canExecute = canExecute;
        _execute = execute;
        _searchTarget = searchTarget;
        _toggleSidebar = toggleSidebar;
        _synchronize = synchronize;
        _briefing = briefing;
        _whatsNew = whatsNew;
        _account = account;
        _error = error;
    }

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
        // Adjacent buttons share one glass capsule; the space gives sync a capsule of its own.
        NSToolbar.NSToolbarSpaceItemIdentifier,
        SyncId,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier
    ];

    private static readonly string[] TrailingIdentifiers =
    [
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        SearchId,
        BriefingId,
        WhatsNewId,
        AccountId
    ];

    private string[] Identifiers() => [.. LeadingIdentifiers, .. CommandIdentifiers(), .. TrailingIdentifiers];

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
    /// Applies the reader action layout (Settings › Reader & Composer › Reader actions): each group keeps
    /// its members in the configured order and drops the hidden ones. Move has no layout entry and stays.
    /// </summary>
    public void SetCommandLayout(IEnumerable<ReaderCommandLayoutEntry> entries)
    {
        var order = new Dictionary<ShellCommand, int>();
        var hidden = new HashSet<ShellCommand>();
        int position = 0;
        foreach (var entry in entries)
        {
            if (ToShellCommand(entry.Action) is not { } command) continue;
            order[command] = position++;
            if (!entry.IsVisible) hidden.Add(command);
        }
        var groups = CommandGroups
            .Select(group => group.Where(command => !hidden.Contains(command))
                .OrderBy(command => order.TryGetValue(command, out var index) ? index : int.MaxValue)
                .ToArray())
            .ToArray();
        if (groups.Select(group => string.Join(',', group)).SequenceEqual(_visibleGroups.Select(group => string.Join(',', group)))) return;
        _visibleGroups = groups;
        RebuildCommandItems();
    }

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
        var items = _toolbar.Items;
        for (int index = items.Length - 1; index >= LeadingIdentifiers.Length; index--)
        {
            var id = items[index].Identifier;
            if (id.StartsWith(CommandIdPrefix, StringComparison.Ordinal) || id == NSToolbar.NSToolbarSpaceItemIdentifier)
                _toolbar.RemoveItem(index);
        }
        _commandItems.Clear();
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

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Identifiers();
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
            case SyncId:
                return _syncItem = CreateSyncItem();
            case SearchId:
                return _searchItem = CreateSearchItem();
            case BriefingId:
                return _briefingItem = CreateBriefingItem();
            case WhatsNewId:
                _whatsNewItem = Button(WhatsNewId, WinoIconGlyph.Announcement, Translator.WhatsNew_ShellButtonTooltip, () => _whatsNew());
                _whatsNewItem.Hidden = !_whatsNewVisible;
                return _whatsNewItem;
            case var id when id.StartsWith(CommandIdPrefix, StringComparison.Ordinal):
                return CreateCommandItem(id);
            case AccountId:
                _accountItem = Button(AccountId, WinoIconGlyph.Person, Translator.WinoAccount_Titlebar_SignedOutTitle, null);
                var view = (NSButton)_accountItem.View!;
                view.Activated += (_, _) => Safe(() => _account(view));
                _accountItem.Hidden = _accountHidden;
                return _accountItem;
        }
        return new NSToolbarItem(itemIdentifier);
    }

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
        button.Activated += (_, _) => Safe(() => _execute(entry.Command, button));
        _commandItems[entry.Command] = item;
        ApplyCommandState(entry.Command, item);
        return item;
    }

    private void ApplyCommandState(ShellCommand command, NSToolbarItem item)
    {
        item.Hidden = !_hasCommandTarget();
        if (item.View is NSButton button) button.Enabled = _canExecute(command);
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception exception) { _error(exception); }
    }

    private NSToolbarItem CreateSyncItem()
    {
        var label = "Synchronize";
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
        var container = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        container.AddSubview(_syncButton);
        container.AddSubview(_syncProgress);
        container.AddSubview(_relayFallbackDot);
        NSLayoutConstraint.ActivateConstraints(
        [
            _syncButton.LeadingAnchor.ConstraintEqualTo(container.LeadingAnchor),
            _syncButton.TrailingAnchor.ConstraintEqualTo(container.TrailingAnchor),
            _syncButton.TopAnchor.ConstraintEqualTo(container.TopAnchor),
            _syncButton.BottomAnchor.ConstraintEqualTo(container.BottomAnchor),
            _syncProgress.CenterXAnchor.ConstraintEqualTo(_syncButton.CenterXAnchor),
            _syncProgress.CenterYAnchor.ConstraintEqualTo(_syncButton.CenterYAnchor),
            _relayFallbackDot.TrailingAnchor.ConstraintEqualTo(_syncButton.TrailingAnchor, -5),
            _relayFallbackDot.TopAnchor.ConstraintEqualTo(_syncButton.TopAnchor, 4)
        ]);
        item.View = container;
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
            _syncButton.ToolTip = active ? $"{Translator.Relay_FallbackFlyoutTitle}. {Translator.Relay_FallbackFlyoutMessage}" : "Synchronize";
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
        item.Hidden = !_briefingVisible;
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

    /// <summary>Re-reads search support from the current content page.</summary>
    public void Revalidate()
    {
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
        _syncItem.Hidden = !supported;
        _syncButton.Enabled = canSynchronize && !isSynchronizing;
        _syncButton.Image = isSynchronizing ? null : WinoIcons.Image(WinoIconGlyph.Sync, 17, null, "Synchronize");
        if (isSynchronizing) _syncProgress.StartAnimation(null); else _syncProgress.StopAnimation(null);
        var tip = string.IsNullOrWhiteSpace(toolTip) ? "Synchronize" : toolTip;
        _syncItem.ToolTip = tip;
        _syncButton.ToolTip = tip;
        WinoAccessibility.Label(_syncButton, tip);
    }

    public void SetBriefing(bool visible, bool hasUnseen, bool isOpen)
    {
        _briefingVisible = visible;
        _briefingUnseen = hasUnseen;
        _briefingOpen = isOpen;
        if (_briefingItem is not null) _briefingItem.Hidden = !visible;
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
        if (_whatsNewItem is not null) _whatsNewItem.Hidden = !visible;
    }

    public void SetAccountHidden(bool hidden)
    {
        _accountHidden = hidden;
        if (_accountItem is not null) _accountItem.Hidden = hidden;
    }
}
