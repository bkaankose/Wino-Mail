using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// The unified window toolbar in the Windows title bar order: sidebar toggle, synchronization
/// (navigational, so it precedes the title), the window title and subtitle (native), a centred search field,
/// then Daily briefing, What's New and the Wino account. Mail actions live in the reader command
/// bar and the Message menu, as on Windows. Every glyph comes from the WinoIcons font.
/// </summary>
internal sealed class ShellToolbar : NSToolbarDelegate
{
    private const string ToggleSidebarId = "wino.toggleSidebar";
    private const string SyncId = "wino.sync";
    private const string SearchId = "wino.search";
    private const string BriefingId = "wino.briefing";
    private const string WhatsNewId = "wino.whatsNew";
    private const string AccountId = "wino.account";

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
        Action<NSView> briefing, Action whatsNew, Action<NSView> account, Action<Exception> error)
    {
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
            AutosavesConfiguration = false,
            CenteredItemIdentifier = SearchId
        };
        return toolbar;
    }

    private static string[] Identifiers() =>
    [
        ToggleSidebarId,
        // Adjacent buttons share one glass capsule; the space gives sync a capsule of its own.
        NSToolbar.NSToolbarSpaceItemIdentifier,
        SyncId,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        SearchId,
        NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
        BriefingId,
        WhatsNewId,
        AccountId
    ];

    /// <summary>
    /// The space before sync must be navigational too, or AppKit moves it after the window title
    /// and the sync button joins the sidebar toggle's capsule.
    /// </summary>
    public override void WillAddItem(NSNotification notification)
    {
        if (notification.UserInfo?["item"] is NSToolbarItem { Identifier: var identifier } item && identifier == NSToolbar.NSToolbarSpaceItemIdentifier)
            item.Navigational = true;
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
            PreferredWidthForSearchField = 390,
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
