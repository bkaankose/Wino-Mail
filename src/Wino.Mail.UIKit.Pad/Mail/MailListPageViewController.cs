using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Shell;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;
using Wino.Presentation.UIKit;
using Wino.Mail.UIKit.Views.Mail;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// Mail mode content page in the iPad list column (the Mac MailListPageViewController without its reading pane, which
/// is the shell's reader column, <see cref="PadRenderingFrameHost"/>): header with pivots, select mode and filters,
/// the bars, the search scope bar and the mail table over the shared projection, inside a Wino zone. The page routes
/// the selection to the reader column through the router (MailRenderingPage, ComposePage, IdlePage) and shows the
/// multiple-selection summary there. Partial files: List (table, projection, selection, swipe, drag), Commands (shell
/// commands, context menus, Move popover), Search (toolbar search and scope bar) and Bars.
/// </summary>
public sealed partial class MailListPageViewController : WinoViewController<MailListPageViewModel>,
    IShellCommandTarget,
    IMailMovePresenter,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<DisposeRenderingFrameRequested>,
    IRecipient<ComposeDetachedDraftRequested>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<WinoIntelligenceEntitlementChanged>
{
    private readonly UIKitNavigationService _navigation;
    private readonly IFolderService _folderService;
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly UIKitSenderPictureProvider _pictures;
    private readonly PadShellHost _host;
    private readonly PadMailMovePresenter _movePresenter;
    private readonly WinoZoneView _listZone = new() { CornerRadius = PadRenderingFrameHost.ZoneRadius };
    // The shared navigation contract declares a non-nullable parameter; this ViewModel ignores it, so a page without one
    // passes this placeholder instead of null.
    private bool _registered;
    private bool _released;
    private bool _detachedComposerActive;

    public MailListPageViewController(MailListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        UIKitNavigationService navigation, IFolderService folderService, IWinoAccountIntelligenceSnapshotService entitlementService,
        UIKitSenderPictureProvider pictures, PadShellHost host, PadMailMovePresenter movePresenter)
        : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _folderService = folderService;
        _entitlementService = entitlementService;
        _pictures = pictures;
        _host = host;
        _movePresenter = movePresenter;
        _preferences = viewModel.PreferencesService;
    }

    /// <summary>The shell's reader column, while this page is the shell content.</summary>
    private PadRenderingFrameHost? Frame => (_navigation.Shell as PadShellViewController)?.ReaderFrame;

    public override void LoadView()
    {
        ViewModel.MailCollection.CoreDispatcher = Dispatcher;

        var root = new UIView { BackgroundColor = UIColor.Clear };
        root.AddSubview(_listZone);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            _listZone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
            _listZone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
            _listZone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, 0),
            _listZone.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
        ]);
        WinoLayout.Fill(BuildListPane(), _listZone.ContentView);
        View = root;
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        RegisterRecipients();
        _movePresenter.Attach(this);
        BindList();
        BindSearch();
        AttachMultiSelection();
        ViewModel.OnNavigatedTo(mode, parameter);
        ShowIdleContent();
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
    }

    protected override async Task DeactivateAsync()
    {
        UnregisterRecipients();
        _movePresenter.Detach(this);
        DetachMultiSelection();
        CloseSearchFilters();
        ReleaseList();
        await ViewModel.DeactivateAsync(NavigationMode.New, null);
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        _searchCancellation.Cancel();
        await base.ReleaseAsync();
    }

    private void RegisterRecipients()
    {
        if (_registered) return;
        _registered = true;
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<ActiveMailItemChangedEvent>(this);
        messenger.Register<ClearMailSelectionsRequested>(this);
        messenger.Register<DisposeRenderingFrameRequested>(this);
        messenger.Register<ComposeDetachedDraftRequested>(this);
        messenger.Register<SelectMailItemContainerEvent>(this);
        messenger.Register<WinoIntelligenceEntitlementChanged>(this);
        Observe(RefreshIntelligenceEntitlementAsync());
    }

    private void UnregisterRecipients()
    {
        if (!_registered) return;
        _registered = false;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private void OnUI(Action action)
    {
        if (_released) return;
        _ = Dispatcher.ExecuteOnUIThread(() => { if (!_released) action(); });
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    #region Reader column (Mac ReadingPane)

    /// <summary>The idle page when it is routed; otherwise the reader column empties itself.</summary>
    private void ShowIdleContent()
    {
        if (_released) return;
        if (_navigation.GetPageType(WinoPage.IdlePage) is not null)
        {
            ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
            return;
        }
        if (Frame is { } frame) Observe(frame.ClearAsync());
    }

    private void ApplyActiveMailItemChange(MailItemViewModel? item)
    {
        if (item is null)
        {
            // A detached draft opened from elsewhere replaces the selection; keep it on screen.
            if (_detachedComposerActive) return;
            ShowIdleContent();
            return;
        }

        _detachedComposerActive = false;
        ViewModel.OpenItem(item);
    }

    private void AttachMultiSelection()
    {
        if (Frame?.MultiSelection is not { } view) return;
        view.ActionInvoked += MultiSelectionAction;
        view.UnselectAllInvoked += UnselectAll;
    }

    private void DetachMultiSelection()
    {
        if (Frame?.MultiSelection is not { } view) return;
        view.ActionInvoked -= MultiSelectionAction;
        view.UnselectAllInvoked -= UnselectAll;
        view.Hidden = true;
    }

    private void MultiSelectionAction(object? sender, MailBulkAction action) => Execute(action switch
    {
        MailBulkAction.Archive => ShellCommand.Archive,
        MailBulkAction.Delete => ShellCommand.Delete,
        MailBulkAction.Flag => ShellCommand.Flag,
        MailBulkAction.ToggleRead => ShellCommand.ToggleRead,
        _ => ShellCommand.Move
    }, sender as UIView);

    private void UnselectAll(object? sender, EventArgs args) => ViewModel.UnselectAllCommand.Execute(null);

    private void UpdateMultiSelectionOverlay()
    {
        if (Frame?.MultiSelection is not { } overlay) return;
        bool show = ViewModel.SelectedItemsCount > 1 && !ViewModel.HasSingleFullySelectedThread;
        overlay.Hidden = !show;
        if (!show) return;

        var items = ViewModel.SelectedItems;
        var senders = items.Select(static item => (item.FromName ?? string.Empty, item.FromAddress ?? string.Empty))
            .DistinctBy(static sender => sender.Item2, StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        overlay.Update(senders, items.Count, items.Count(static item => !item.IsRead),
            items.Count(static item => item.IsFlagged), items.Count(static item => item.HasAttachments), _pictures.Get);
    }

    #endregion

    #region Messages

    public void Receive(ActiveMailItemChangedEvent message)
    {
        var item = message.SelectedMailItemViewModel;
        OnUI(() => ApplyActiveMailItemChange(item));
    }

    public void Receive(ClearMailSelectionsRequested message) => OnUI(DeselectAll);

    public void Receive(DisposeRenderingFrameRequested message)
        => OnUI(() =>
        {
            _detachedComposerActive = false;
            ShowIdleContent();
        });

    public void Receive(ComposeDetachedDraftRequested message)
    {
        if (message.Draft is null) return;
        OnUI(() =>
        {
            // The draft is not listed: drop the selection and host the composer on its own.
            _detachedComposerActive = true;
            DeselectAll();
            ViewModel.NavigationService.Navigate(WinoPage.ComposePage, message.Draft, NavigationReferenceFrame.RenderingFrame);
        });
    }

    public void Receive(SelectMailItemContainerEvent message) => Observe(SelectItemWhenReadyAsync(message));

    private async Task SelectItemWhenReadyAsync(SelectMailItemContainerEvent message)
    {
        for (int attempt = 0; attempt < 15 && !_released; attempt++)
        {
            bool selected = false;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (_released || _projection is null) return;
                if (IndexPathOf(row => row.SourceItem.StableId == message.MailUniqueId
                    || (row.IsThreadHead && row.LeafItems.Any(leaf => leaf.StableId == message.MailUniqueId))) is not { } path) return;
                _table.SelectRow(path, false, message.ScrollToItem ? UITableViewScrollPosition.Middle : UITableViewScrollPosition.None);
                SyncSelectionFromTable();
                selected = true;
            });
            if (selected) return;
            await Task.Delay(200);
        }
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            _searchCancellation.Cancel();
            UnregisterRecipients();
            _movePresenter.Detach(this);
            CloseSearchFilters();
            ReleaseList();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// <see cref="IMailMovePresenter"/> for the reader command bar: forwards to the mail page that is on screen, which owns
/// the Move popover and the list's operation path.
/// </summary>
public sealed class PadMailMovePresenter : IMailMovePresenter
{
    private MailListPageViewController? _page;

    internal void Attach(MailListPageViewController page) => _page = page;

    internal void Detach(MailListPageViewController page)
    {
        if (ReferenceEquals(_page, page)) _page = null;
    }

    public bool TryShowMove(IReadOnlyList<Wino.Core.Domain.Entities.Mail.MailCopy> mails, UIView anchor)
        => _page is { } page && page.TryShowMove(mails, anchor);
}
