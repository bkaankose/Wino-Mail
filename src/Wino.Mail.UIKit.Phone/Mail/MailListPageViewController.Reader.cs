using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.UIKit.Phone.Mail;

/// <summary>
/// The phone has no reading pane: the shared selection's active item opens the shared reader page pushed on the Mail
/// stack (the registry routes <see cref="WinoPage.MailRenderingPage"/> to a push), and a draft opens the composer as a
/// full-screen sheet. When the open message leaves the list (deleted, moved or archived from the reader toolbar) the
/// reader pops back to the list; coming back to the list clears the selection, so a tap opens a message again.
/// </summary>
public sealed partial class MailListPageViewController
{
    public void Receive(ActiveMailItemChangedEvent message)
    {
        var item = message.SelectedMailItemViewModel;
        OnUI(() => ApplyActiveMailItemChange(item));
    }

    private void ApplyActiveMailItemChange(MailItemViewModel? item)
    {
        if (item is null)
        {
            PopToList(animated: true);
            return;
        }
        // Edit mode selects without opening.
        if (_selectionMode) return;
        PopToList(animated: false);
        ViewModel.NavigationService.Navigate(item.IsDraft ? WinoPage.ComposePage : WinoPage.MailRenderingPage, item, NavigationReferenceFrame.RenderingFrame);
    }

    /// <summary>True while a page this list opened (the reader) is on top of it.</summary>
    internal bool IsCoveredByReader
        => NavigationController is { } navigation && !ReferenceEquals(navigation.TopViewController, this)
           && Array.IndexOf(navigation.ViewControllers ?? [], this) >= 0;

    private void PopToList(bool animated)
    {
        if (IsCoveredByReader && NavigationController is { } navigation) navigation.PopToViewController(this, animated);
    }

    public void Receive(ClearMailSelectionsRequested message) => OnUI(ClearSelection);

    public void Receive(DisposeRenderingFrameRequested message) => OnUI(() => PopToList(animated: true));

    public void Receive(ComposeDetachedDraftRequested message)
    {
        if (message.Draft is not { } draft) return;
        OnUI(() =>
        {
            // The draft is not listed: drop the selection and open the composer on its own.
            ClearSelection();
            ViewModel.NavigationService.Navigate(WinoPage.ComposePage, draft, NavigationReferenceFrame.RenderingFrame);
        });
    }

    public void Receive(SelectMailItemContainerEvent message) => Observe(SelectItemWhenReadyAsync(message));

    /// <summary>Selects a mail once the list shows it (a new draft, a notification), retrying while the folder loads.</summary>
    private async Task SelectItemWhenReadyAsync(SelectMailItemContainerEvent message)
    {
        for (int attempt = 0; attempt < 15 && !_released; attempt++)
        {
            bool selected = false;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (_released || _projection is null) return;
                foreach (var (indexPath, row) in DisplayedRows())
                {
                    if (row.SourceItem.StableId != message.MailUniqueId && !(row.IsThreadHead && row.LeafItems.Any(leaf => leaf.StableId == message.MailUniqueId))) continue;
                    if (!_selectionMode) foreach (var other in _table.IndexPathsForSelectedRows ?? []) _table.DeselectRow(other, false);
                    _table.SelectRow(indexPath, false, message.ScrollToItem ? UITableViewScrollPosition.Middle : UITableViewScrollPosition.None);
                    SyncSelectionFromTable();
                    selected = true;
                    return;
                }
            });
            if (selected) return;
            await Task.Delay(200);
        }
    }

    // ---- Wino Intelligence entitlement (Windows MailListPage.ApplyIntelligenceEntitlementAsync) ----

    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        bool canAccess = message.Entitlement.CanAccessSurfaces;
        OnUI(() => Observe(ApplyIntelligenceEntitlementAsync(canAccess)));
    }

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
        // A row can gain or lose its tile line; heights are re-queried for every row.
        if (changed && !_released && _listBound) NoteHeightsChanged();
    }
}
