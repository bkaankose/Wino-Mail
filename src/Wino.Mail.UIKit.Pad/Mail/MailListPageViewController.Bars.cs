using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Messaging.UI;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// The bars around the mail list (Windows MailListPage, the Mac Bars partial): the optional action bar above the header,
/// the "folder synchronization disabled" info bar, the "unread mail in Other" link, the undo bar over the bottom of the
/// list, and the Wino Intelligence entitlement refresh.
/// </summary>
public sealed partial class MailListPageViewController
{
    private UIView _actionBarRow = null!;
    private UIStackView _actionBar = null!;
    private UIView _syncBarRow = null!;
    private WinoInfoBar _syncBar = null!;
    private bool _syncBarDismissed;
    private object? _syncBarFolder;
    private UIView _otherInboxRow = null!;
    private UIButton _otherInboxButton = null!;
    private WinoInfoBar _undoBar = null!;
    private WinoSurfaceView _undoBarHost = null!;

    #region Action bar (Windows MailListPage.xaml ActionBarGrid)

    /// <summary>Icon-only buttons over <see cref="Wino.Mail.ViewModels.MailListPageViewModel.ActionItems"/>; shown while the preference is on.</summary>
    private UIView BuildActionBar()
    {
        _actionBar = WinoLayout.Insets(WinoLayout.HStack(2), 2, 4, 2, 4);
        _actionBar.Alignment = UIStackViewAlignment.Center;
        var host = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, CornerRadius = WinoStyle.GroupRadius };
        var scroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false };
        WinoLayout.Fill(_actionBar, scroll);
        _actionBar.HeightAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.HeightAnchor).Active = true;
        WinoLayout.Fill(scroll, host);
        WinoLayout.Size(host, -1, 44);
        host.AccessibilityTraits = UIAccessibilityTrait.None;
        WinoAccessibility.Label(host, Translator.MacOS_MailList_ActionBar);
        _actionBarRow = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(host, _actionBarRow, 0, 0, 5, 0);
        _actionBarRow.Hidden = !_preferences.IsMailListActionBarEnabled;
        return _actionBarRow;
    }

    private void UpdateActionBar()
    {
        bool enabled = _preferences.IsMailListActionBarEnabled;
        _actionBarRow.Hidden = !enabled;
        if (!enabled) return;

        foreach (var view in _actionBar.ArrangedSubviews)
        {
            _actionBar.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        bool hasSelection = ViewModel.SelectedItemsCount > 0;
        bool previousSeparator = true;
        foreach (var item in ViewModel.ActionItems.OfType<MailOperationMenuItem>())
        {
            if (item.Operation == MailOperation.Seperator)
            {
                if (!previousSeparator) _actionBar.AddArrangedSubview(WinoLayout.Size(new WinoSeparator(vertical: true), 1, 22));
                previousSeparator = true;
                continue;
            }
            var title = item.Operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var captured = item;
            UIButton? button = null;
            button = GlyphButton(MailOperationDisplay.GetGlyph(item.Operation), 18, title, () =>
            {
                // Move opens the folder popover at the tapped button instead of the shared picker.
                if (captured.Operation == MailOperation.Move && button is not null) ShowMovePopover(ViewModel.SelectedItems.ToArray(), button);
                else Observe(ViewModel.ExecuteTopBarActionCommand.ExecuteAsync(captured));
            });
            button.Enabled = item.IsEnabled && hasSelection;
            _actionBar.AddArrangedSubview(button);
            previousSeparator = false;
        }
    }

    #endregion

    #region Folder synchronization disabled (Windows SyncDisabledInfoBar)

    private UIView BuildSyncBar()
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
        _syncBarRow = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
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

    #endregion

    #region Unread mail in Other (Windows OtherInboxUnreadNoticeLink)

    private UIView BuildOtherInboxLink()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.TitleAlignment = UIButtonConfigurationTitleAlignment.Leading;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(4, 4, 4, 4);
        _otherInboxButton = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        _otherInboxButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        _otherInboxButton.TitleLabel.Lines = 2;
        _otherInboxButton.PrimaryActionTriggered += (_, _) => Observe(ViewModel.OpenOtherInboxCommand.ExecuteAsync(null));
        _otherInboxRow = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_otherInboxButton, _otherInboxRow, 0, 4, 6, 4);
        return _otherInboxRow;
    }

    /// <summary>Accent-coloured views that do not follow the accent change themselves.</summary>
    private void RefreshBarsAccent()
    {
        if (_otherInboxButton.Configuration is not { } configuration) return;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        _otherInboxButton.Configuration = configuration;
    }

    private void UpdateOtherInboxLink()
    {
        bool show = ViewModel.IsOtherInboxUnreadNoticeVisible;
        _otherInboxRow.Hidden = !show;
        if (!show || _otherInboxButton.Configuration is not { } configuration) return;

        configuration.Title = ViewModel.OtherInboxUnreadNoticeText ?? string.Empty;
        _otherInboxButton.Configuration = configuration;
    }

    #endregion

    #region Undo bar (Windows MailListPage undo WinoInfoBar; Cmd+Z is in the Commands partial)

    private WinoSurfaceView BuildUndoBar()
    {
        _undoBar = new WinoInfoBar { Hidden = true, IsClosable = false, ActionTitle = Translator.UndoActions_Button };
        _undoBar.ActionInvoked += (_, _) => Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
        _undoBar.Closed += (_, _) => ViewModel.IsUndoMailActionBarOpen = false;
        _undoBarHost = FloatingHost(_undoBar);
        return _undoBarHost;
    }

    private void UpdateUndoBar()
    {
        bool open = ViewModel.IsUndoMailActionBarOpen;
        _undoBar.Title = ViewModel.UndoMailActionBarTitle;
        _undoBar.Severity = MapSeverity(ViewModel.UndoMailActionBarSeverity);
        _undoBar.AutoDismissInterval = TimeSpan.FromSeconds(ViewModel.UndoMailActionBarDismissInterval);
        // Host first: the bar starts its auto-dismiss countdown only when it is really visible.
        _undoBarHost.Hidden = !open;
        _undoBar.Hidden = !open;
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

    #endregion

    #region Wino Intelligence entitlement (Windows MailListPage.ApplyIntelligenceEntitlementAsync)

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
        // Visible cells re-map through their item subscription; every row is re-measured, since one can lose its tile line.
        if (!changed || _released || !_listBound || _lastRowCount == 0) return;
        NoteHeightsChanged();
    }

    #endregion

    private void BindBars()
    {
        Bind(nameof(ViewModel.ActionItems), vm => vm.ActionItems, _ => UpdateActionBar());
        Bind(nameof(ViewModel.SelectedItemsCount), vm => vm.SelectedItemsCount, _ => UpdateActionBar());
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

    /// <summary>Preference changes the bars follow live.</summary>
    private void BarsPreferenceChanged(string name)
    {
        if (name == nameof(_preferences.IsMailListActionBarEnabled)) UpdateActionBar();
        else if (name == nameof(_preferences.IsOtherInboxUnreadNoticeEnabled)) UpdateOtherInboxLink();
    }
}
