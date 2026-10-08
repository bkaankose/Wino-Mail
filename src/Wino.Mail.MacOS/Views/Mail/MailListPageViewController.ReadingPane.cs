using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>The mail page's reading pane, as seen by a hosted page that can leave and return (the composer).</summary>
public interface IReadingPaneHost
{
    bool IsAvailable { get; }

    /// <summary>Removes the child from the pane without releasing it; the pane shows the idle page.</summary>
    Task PopOutAsync(NSViewController child);

    /// <summary>Puts an already active child back into the pane, releasing whatever the pane showed.</summary>
    Task DockAsync(NSViewController child);

    /// <summary>
    /// A popped-out reader started Reply, Reply all or Forward: the composer for <paramref name="draftUniqueId"/>
    /// opens in its own window too (Windows PopoutHostActionKind.PopOutNextNavigation).
    /// </summary>
    void PopOutNextComposer(Guid draftUniqueId);
}

/// <summary>Implemented by reading pane pages that need their host, such as the detachable composer.</summary>
public interface IReadingPaneChild
{
    IReadingPaneHost? PaneHost { get; set; }
}

public sealed partial class MailListPageViewController : IRenderingFrameHost, IReadingPaneHost
{
    private readonly SemaphoreSlim _paneGate = new(1, 1);
    private NSView _paneContent = null!;
    private MailMultiSelectionView _multiSelectionView = null!;
    private NSViewController? _paneChild;
    private int _paneVersion;
    private bool _detachedComposerActive;
    private Guid? _popOutNextDraftId;

    private NSView BuildReadingPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _paneContent = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_paneContent, pane);
        _multiSelectionView = new MailMultiSelectionView { Hidden = true };
        var overlay = new WinoSurfaceView { Fill = null, Hidden = true };
        WinoLayout.Fill(_multiSelectionView, overlay);
        _multiSelectionView.Hidden = false;
        WinoLayout.Fill(overlay, pane);
        _multiSelectionView.ActionInvoked += (sender, command) => Execute(command, sender as NSView);
        return pane;
    }

    bool IReadingPaneHost.IsAvailable => !_released;

    public async Task ShowAsync(NSViewController controller, object? parameter)
    {
        ComposePageViewController? popOutComposer = null;
        int version = Interlocked.Increment(ref _paneVersion);
        await _paneGate.WaitAsync();
        try
        {
            if (_released || version != Volatile.Read(ref _paneVersion))
            {
                await Dispatcher.ExecuteOnUIThread(controller.Dispose);
                return;
            }

            // The reader is reused across selections, like the Windows rendering frame: the new
            // message loads behind the loading treatment instead of a fresh web view per click.
            if (_paneChild is MailRenderingPageViewController reader && controller is MailRenderingPageViewController)
            {
                await Dispatcher.ExecuteOnUIThread(controller.Dispose);
                await reader.RenavigateAsync(parameter);
                return;
            }
            if (_paneChild is IdlePageViewController && controller is IdlePageViewController && parameter is null)
            {
                await Dispatcher.ExecuteOnUIThread(controller.Dispose);
                return;
            }

            await ReplacePaneChildAsync(controller);
            if (controller is IWinoViewController next) await next.ActivateAsync(NavigationMode.New, parameter);
            popOutComposer = TakePendingPopOut(controller, parameter);
        }
        finally { _paneGate.Release(); }
        // Outside the gate: popping out takes it again.
        if (popOutComposer is not null) Observe(popOutComposer.PopOutFromHostAsync());
    }

    void IReadingPaneHost.PopOutNextComposer(Guid draftUniqueId) => _popOutNextDraftId = draftUniqueId;

    /// <summary>The composer a popped-out reader asked for, once it is active in the pane.</summary>
    private ComposePageViewController? TakePendingPopOut(NSViewController controller, object? parameter)
    {
        if (_popOutNextDraftId is not { } pending || controller is not ComposePageViewController composer) return null;
        if (parameter is not MailItemViewModel draft || draft.MailCopy?.UniqueId != pending) return null;
        _popOutNextDraftId = null;
        return composer;
    }

    public async Task ClearAsync()
    {
        Interlocked.Increment(ref _paneVersion);
        await _paneGate.WaitAsync();
        try { await ReplacePaneChildAsync(null); }
        finally { _paneGate.Release(); }
    }

    /// <summary>Releases the current child (asynchronously, so drafts save) and attaches the next one. Caller holds the gate.</summary>
    private async Task ReplacePaneChildAsync(NSViewController? next)
    {
        var previous = _paneChild;
        if (previous is IWinoViewController old)
        {
            try { await old.ReleaseAsync(); }
            catch (Exception exception) { ReportError(exception); }
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (previous is not null)
            {
                previous.View.RemoveFromSuperview();
                previous.RemoveFromParentViewController();
                if (previous is IReadingPaneChild child) child.PaneHost = null;
                previous.Dispose();
            }
            _paneChild = next;
            if (next is null) return;
            if (next is IReadingPaneChild paneChild) paneChild.PaneHost = this;
            AddChildViewController(next);
            WinoLayout.Fill(next.View, _paneContent);
            UpdateMultiSelectionOverlay();
        });
    }

    async Task IReadingPaneHost.PopOutAsync(NSViewController child)
    {
        await _paneGate.WaitAsync();
        try
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!ReferenceEquals(_paneChild, child)) return;
                child.View.RemoveFromSuperview();
                child.RemoveFromParentViewController();
                _paneChild = null;
            });
        }
        finally { _paneGate.Release(); }
        _detachedComposerActive = false;
        ShowIdleContent();
    }

    async Task IReadingPaneHost.DockAsync(NSViewController child)
    {
        Interlocked.Increment(ref _paneVersion);
        await _paneGate.WaitAsync();
        try
        {
            if (_released) return;
            var previous = _paneChild;
            if (previous is IWinoViewController old && !ReferenceEquals(previous, child)) await old.ReleaseAsync();
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (previous is not null && !ReferenceEquals(previous, child))
                {
                    previous.View.RemoveFromSuperview();
                    previous.RemoveFromParentViewController();
                    previous.Dispose();
                }
                child.View.RemoveFromSuperview();
                _paneChild = child;
                if (child is IReadingPaneChild paneChild) paneChild.PaneHost = this;
                AddChildViewController(child);
                WinoLayout.Fill(child.View, _paneContent);
            });
        }
        finally { _paneGate.Release(); }
    }

    private async Task ReleaseReadingPaneAsync()
    {
        Interlocked.Increment(ref _paneVersion);
        await _paneGate.WaitAsync();
        try { await ReplacePaneChildAsync(null); }
        finally { _paneGate.Release(); }
    }

    private void DisposeReadingPane()
    {
        var child = _paneChild;
        _paneChild = null;
        if (child is null) return;
        child.View.RemoveFromSuperview();
        child.RemoveFromParentViewController();
        child.Dispose();
    }

    private void ShowIdleContent()
    {
        if (_released) return;
        ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
    }

    private void ApplyActiveMailItemChange(MailItemViewModel? item)
    {
        if (item is null)
        {
            // A detached draft opened from elsewhere replaces the selection; keep it on screen.
            if (_detachedComposerActive) return;
            if (_paneChild is not IdlePageViewController) ShowIdleContent();
            return;
        }

        _detachedComposerActive = false;
        ViewModel.NavigationService.Navigate(item.IsDraft ? WinoPage.ComposePage : WinoPage.MailRenderingPage,
            item, NavigationReferenceFrame.RenderingFrame);
    }

    private void UpdateMultiSelectionOverlay()
    {
        if (_multiSelectionView?.Superview is not { } overlay) return;
        bool show = ViewModel.SelectedItemsCount > 1 && !ViewModel.HasSingleFullySelectedThread;
        overlay.Hidden = !show;
        _paneContent.Hidden = show;
        if (!show) return;
        var items = ViewModel.SelectedItems;
        var senders = items.Select(static item => (item.FromName ?? string.Empty, item.FromAddress ?? string.Empty))
            .DistinctBy(static sender => sender.Item2, StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        _multiSelectionView.Update(senders, items.Count, items.Count(static item => !item.IsRead),
            items.Count(static item => item.IsFlagged), items.Count(static item => item.HasAttachments));
    }

    public void Receive(ActiveMailItemChangedEvent message)
    {
        var item = message.SelectedMailItemViewModel;
        OnUI(() => ApplyActiveMailItemChange(item));
    }

    public void Receive(ClearMailSelectionsRequested message) => OnUI(() => _table.DeselectAll(null));

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
            _table.DeselectAll(null);
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
                int index = _entries.FindIndex(entry => entry.Row is { } row &&
                    (row.SourceItem.StableId == message.MailUniqueId || (row.IsThreadHead && row.LeafItems.Any(leaf => leaf.StableId == message.MailUniqueId))));
                if (index < 0) return;
                _table.SelectRow(index, false);
                if (message.ScrollToItem) _table.ScrollRowToVisible(index);
                selected = true;
            });
            if (selected) return;
            await Task.Delay(200);
        }
    }
}
