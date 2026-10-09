using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Menus;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Messaging.UI;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The bars around the mail list (Windows MailListPage): the optional action bar above the header,
/// the "folder synchronization disabled" info bar, the "unread mail in Other" link, the undo bar
/// over the bottom of the list with Cmd+Z, and the Wino Intelligence entitlement refresh.
/// </summary>
public sealed partial class MailListPageViewController
{
    private static readonly Selector UndoSelector = new("undo:");

    private NSView _actionBarRow = null!;
    private MailReaderCommandBar _actionBar = null!;
    private NSView _syncBarRow = null!;
    private WinoInfoBar _syncBar = null!;
    private bool _syncBarDismissed;
    private object? _syncBarFolder;
    private NSView _otherInboxRow = null!;
    private NSTextField _otherInboxText = null!;
    private MailLinkButton _otherInboxButton = null!;
    private WinoInfoBar _undoBar = null!;
    private WinoSurfaceView _undoBarHost = null!;

    // ---- Action bar (Windows MailListPage.xaml ActionBarGrid) ----

    /// <summary>Icon-only command bar over <see cref="Wino.Mail.ViewModels.MailListPageViewModel.ActionItems"/>; shown while the preference is on.</summary>
    private NSView BuildActionBar()
    {
        _actionBar = new MailReaderCommandBar { IconOnly = true };
        var host = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(_actionBar, host);
        host.AccessibilityElement = true;
        host.AccessibilityRole = NSAccessibilityRoles.ToolbarRole;
        WinoAccessibility.Label(host, Translator.MacOS_MailList_ActionBar);
        _actionBarRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(host, _actionBarRow, 0, 0, 5, 0);
        _actionBarRow.Hidden = !_preferences.IsMailListActionBarEnabled;
        return _actionBarRow;
    }

    private void UpdateActionBar()
    {
        bool enabled = _preferences.IsMailListActionBarEnabled;
        _actionBarRow.Hidden = !enabled;
        if (!enabled) return;

        bool hasSelection = ViewModel.SelectedItemsCount > 0;
        var commands = new List<MailReaderCommand?>();
        foreach (var item in ViewModel.ActionItems.OfType<MailOperationMenuItem>())
        {
            if (item.Operation == MailOperation.Seperator)
            {
                if (commands.Count > 0 && commands[^1] is not null) commands.Add(null);
                continue;
            }
            var title = item.Operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationPresentation.Title(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var captured = item;
            // Move opens the folder popover at the clicked button instead of the shared picker.
            Action<NSView>? runFrom = item.Operation == MailOperation.Move
                ? anchor => ShowMovePopover(ViewModel.SelectedItems.ToArray(), anchor)
                : null;
            commands.Add(new MailReaderCommand(MailOperationPresentation.Glyph(item.Operation), title,
                () => Observe(ViewModel.ExecuteTopBarActionCommand.ExecuteAsync(captured)), item.IsEnabled && hasSelection, runFrom));
        }
        if (commands.Count > 0 && commands[^1] is null) commands.RemoveAt(commands.Count - 1);
        _actionBar.SetCommands(commands, null);
    }

    // ---- Folder synchronization disabled (Windows SyncDisabledInfoBar) ----

    private NSView BuildSyncBar()
    {
        _syncBar = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.InfoBarTitle_SynchronizationDisabledFolder, Translator.InfoBarMessage_SynchronizationDisabledFolder)
        {
            ActionTitle = Translator.MacOS_MailList_EnableFolderSync,
            IsClosable = true
        };
        _syncBar.ActionInvoked += (_, _) => Observe(ViewModel.EnableFolderSynchronizationCommand.ExecuteAsync(null));
        _syncBar.Closed += (_, _) =>
        {
            _syncBarDismissed = true;
            UpdateSyncBar();
        };
        _syncBarRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_syncBar, _syncBarRow, 0, 4, 6, 4);
        return _syncBarRow;
    }

    /// <summary>Shown while the active folder does not synchronize; closing it lasts until the folder changes.</summary>
    private void UpdateSyncBar()
    {
        var folder = ViewModel.ActiveFolder;
        if (!ReferenceEquals(folder, _syncBarFolder))
        {
            _syncBarFolder = folder;
            _syncBarDismissed = false;
        }
        bool show = folder is not null && ViewModel.IsSyncButtonVisible && !ViewModel.IsFolderSynchronizationEnabled && !_syncBarDismissed;
        _syncBar.Hidden = !show;
        _syncBarRow.Hidden = !show;
    }

    // ---- Unread mail in Other (Windows OtherInboxUnreadNoticeLink) ----

    /// <summary>
    /// A wrapping accent label under a transparent link button that covers it: a wrapping NSButton
    /// title does not report a width-dependent height inside the stack, so this uses the overlay
    /// pattern of the online search panel.
    /// </summary>
    private NSView BuildOtherInboxLink()
    {
        _otherInboxText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.Accent, 2);
        _otherInboxText.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_otherInboxText, host);
        _otherInboxButton = new MailLinkButton(string.Empty) { Transparent = true };
        _otherInboxButton.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _otherInboxButton.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        _otherInboxButton.Activated += (_, _) => Observe(ViewModel.OpenOtherInboxCommand.ExecuteAsync(null));
        WinoLayout.Fill(_otherInboxButton, host);
        _otherInboxRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(host, _otherInboxRow, 0, 8, 6, 8);
        return _otherInboxRow;
    }

    private void UpdateOtherInboxLink()
    {
        bool show = ViewModel.IsOtherInboxUnreadNoticeVisible;
        _otherInboxRow.Hidden = !show;
        if (!show) return;
        var text = ViewModel.OtherInboxUnreadNoticeText ?? string.Empty;
        _otherInboxText.StringValue = text;
        _otherInboxText.TextColor = WinoStyle.Accent;
        WinoAccessibility.Label(_otherInboxButton, text);
    }

    // ---- Undo bar and Cmd+Z (Windows MailListPage undo WinoInfoBar and the Ctrl+Z accelerator) ----

    private WinoSurfaceView BuildUndoBar()
    {
        _undoBar = new WinoInfoBar { Hidden = true, IsClosable = false, ActionTitle = Translator.UndoActions_Button };
        _undoBar.ActionInvoked += (_, _) => Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
        _undoBar.Closed += (_, _) => ViewModel.IsUndoMailActionBarOpen = false;
        _undoBarHost = new WinoSurfaceView { Fill = NSColor.WindowBackground, CornerRadius = WinoStyle.GroupRadius, Hidden = true };
        _undoBarHost.Shadow = new NSShadow { ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.22f), ShadowBlurRadius = 12, ShadowOffset = new CGSize(0, -3) };
        WinoLayout.Fill(_undoBar, _undoBarHost);
        return _undoBarHost;
    }

    private void UpdateUndoBar()
    {
        bool open = ViewModel.IsUndoMailActionBarOpen;
        _undoBar.Title = ViewModel.UndoMailActionBarTitle;
        _undoBar.Severity = MapSeverity(ViewModel.UndoMailActionBarSeverity);
        _undoBar.AutoDismissInterval = TimeSpan.FromSeconds(ViewModel.UndoMailActionBarDismissInterval);
        _undoBar.Hidden = !open;
        _undoBarHost.Hidden = !open;
    }

    /// <summary>A new pack or interval while the bar is up gets a full countdown again.</summary>
    private void RestartUndoCountdown()
    {
        UpdateUndoBar();
        if (ViewModel.IsUndoMailActionBarOpen) _undoBar.RestartAutoDismiss();
    }

    private static WinoInfoBarSeverity MapSeverity(InfoBarMessageType severity) => severity switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };

    /// <summary>
    /// Edit › Undo (Cmd+Z) sends undo: up the responder chain. The page answers it only while a queued
    /// mail action can be undone and the focus is not in text (a field editor, or the composer's editor
    /// in the reading pane), so those keep their own undo and Undo is disabled otherwise.
    /// </summary>
    [Export("undo:")]
    public void UndoMailAction(NSObject? sender)
    {
        if (CanUndoMailAction) Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
    }

    public override bool RespondsToSelector(Selector? sel)
    {
        if (sel is not null && sel.Handle == UndoSelector.Handle) return CanUndoMailAction;
        return base.RespondsToSelector(sel);
    }

    /// <summary>Allocation-free: reads fields and ViewModel state only.</summary>
    private bool CanUndoMailAction
    {
        get
        {
            if (_released || !_listBound || !NSThread.IsMain || !ViewLoaded || ViewModel.CurrentVisibleUndoMailActionPack is null) return false;
            var responder = View.Window?.FirstResponder;
            if (responder is NSText) return false;
            return !IsComposerFocused(responder);
        }
    }

    // ---- Wino Intelligence entitlement (Windows MailListPage.ApplyIntelligenceEntitlementAsync) ----

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        bool canAccess = message.Entitlement.CanAccessSurfaces;
        OnUI(() => Observe(ApplyIntelligenceEntitlementAsync(canAccess)));
    }

    /// <summary>The cached entitlement can be stale when the page opens, so it is fetched once on registration.</summary>
    private async Task RefreshIntelligenceEntitlementAsync()
    {
        var entitlement = await _entitlementService.GetEntitlementAsync().ConfigureAwait(false);
        bool canAccess = entitlement.CanAccessSurfaces;
        OnUI(() => Observe(ApplyIntelligenceEntitlementAsync(canAccess)));
    }

    private async Task ApplyIntelligenceEntitlementAsync(bool canAccess)
    {
        if (_released) return;
        bool changed = false;
        foreach (var item in ViewModel.MailCollection.Items)
        {
            if (item.CanShowIntelligence == canAccess) continue;
            changed = true;
            break;
        }
        await ViewModel.ApplyIntelligenceEntitlementAsync(canAccess);
        // Visible cells re-map through their item subscription; off-screen rows keep cached heights
        // until they are re-measured, and a row can lose its tile line, so every row is re-measured.
        if (!changed || _released || !_listBound || _entries.Count == 0) return;
        NoteHeightsChanged(NSIndexSet.FromNSRange(new NSRange(0, _entries.Count)));
    }

    // ---- Bindings ----

    private void BindBars()
    {
        Bind(nameof(ViewModel.ActionItems), vm => vm.ActionItems, _ => UpdateActionBar());
        Bind(nameof(ViewModel.ActiveFolder), vm => vm.ActiveFolder, _ => { UpdateSyncBar(); UpdateOtherInboxLink(); });
        Bind(nameof(ViewModel.IsFolderSynchronizationEnabled), vm => vm.IsFolderSynchronizationEnabled, _ => UpdateSyncBar());
        Bind(nameof(ViewModel.IsSyncButtonVisible), vm => vm.IsSyncButtonVisible, _ => UpdateSyncBar());
        Bind(nameof(ViewModel.IsOtherInboxUnreadNoticeVisible), vm => vm.IsOtherInboxUnreadNoticeVisible, _ => UpdateOtherInboxLink());
        Bind(nameof(ViewModel.OtherInboxUnreadNoticeText), vm => vm.OtherInboxUnreadNoticeText, _ => UpdateOtherInboxLink());
        // The notice also depends on the pivots, which do not always raise it.
        Bind(nameof(ViewModel.SelectedFolderPivot), vm => vm.SelectedFolderPivot, _ => UpdateOtherInboxLink());
        Bind(nameof(ViewModel.IsUndoMailActionBarOpen), vm => vm.IsUndoMailActionBarOpen, _ => UpdateUndoBar());
        Bind(nameof(ViewModel.UndoMailActionBarTitle), vm => vm.UndoMailActionBarTitle, _ => UpdateUndoBar());
        Bind(nameof(ViewModel.UndoMailActionBarSeverity), vm => vm.UndoMailActionBarSeverity, _ => UpdateUndoBar());
        Bind(nameof(ViewModel.UndoMailActionBarDismissInterval), vm => vm.UndoMailActionBarDismissInterval, _ => RestartUndoCountdown());
        Bind(nameof(ViewModel.CurrentVisibleUndoMailActionPack), vm => vm.CurrentVisibleUndoMailActionPack, _ => RestartUndoCountdown());
    }

    /// <summary>Preference changes the bars follow live (Windows loads the action bar once).</summary>
    private void BarsPreferenceChanged(string name)
    {
        if (name == nameof(_preferences.IsMailListActionBarEnabled)) UpdateActionBar();
        else if (name == nameof(_preferences.IsOtherInboxUnreadNoticeEnabled)) UpdateOtherInboxLink();
    }
}
