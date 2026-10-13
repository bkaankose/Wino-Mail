using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Mail;

/// <summary>
/// Mail list drag source (Windows ThreadedMailListView.OnDragItemsStarting / OnDragItemsCompleted, the Mac Drag
/// partial): a row drags its mails (a thread head all of its thread, a selected row the whole selection) as a local
/// <see cref="MailDragPayload"/>, restricted to the app; the ViewModel drag state drives the "Dragging N item(s)" banner.
/// Drops are handled by the sidebar folders.
/// </summary>
public sealed partial class MailListPageViewController
{
    private WinoSurfaceView _dragBanner = null!;
    private UILabel _dragBannerText = null!;

    /// <summary>Adds the drag banner over the list (Windows DraggingMessageBorder: bottom right, card fill, 8pt corners).</summary>
    private void BuildDragBanner(UIView listHost)
    {
        _dragBannerText = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.PrimaryText);
        _dragBanner = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.ZoneStroke, StrokeWidth = 1, CornerRadius = 8, Hidden = true };
        WinoLayout.Fill(_dragBannerText, _dragBanner, 6, 10, 6, 10);
        listHost.AddSubview(_dragBanner);
        NSLayoutConstraint.ActivateConstraints(
        [
            _dragBanner.TrailingAnchor.ConstraintEqualTo(listHost.TrailingAnchor, -14),
            _dragBanner.BottomAnchor.ConstraintEqualTo(listHost.SafeAreaLayoutGuide.BottomAnchor, -14)
        ]);
    }

    private void BindDrag()
    {
        Bind(nameof(ViewModel.IsDragInProgress), vm => vm.IsDragInProgress, _ => UpdateDragBanner());
        Bind(nameof(ViewModel.DraggingItemsCount), vm => vm.DraggingItemsCount, _ => UpdateDragBanner());
#if DEBUG
        MailDragPayload.DebugSelection = () => _released ? [] : ViewModel.SelectedItems.Select(static item => item.MailCopy).ToList();
#endif
    }

    private void UpdateDragBanner()
    {
        _dragBannerText.Text = ViewModel.DraggingMessageText;
        _dragBanner.Hidden = !ViewModel.IsDragInProgress;
    }

    /// <summary>The mails a row stands for: a thread head drags every mail of its thread.</summary>
    private IEnumerable<MailCopy> RowMails(NSIndexPath path)
    {
        if (RowAt(path) is not { } row) yield break;
        var leaves = row.IsThreadHead ? row.LeafItems : [row.SourceItem];
        foreach (var leaf in leaves.OfType<MailItemViewModel>()) yield return leaf.MailCopy;
    }

    /// <summary>A selected row drags the whole selection; any other row drags itself.</summary>
    private List<MailCopy> DraggedMails(NSIndexPath path)
    {
        var selected = _table.IndexPathsForSelectedRows ?? [];
        bool inSelection = selected.Any(candidate => candidate.Section == path.Section && candidate.Row == path.Row);
        var paths = inSelection ? selected : [path];
        var seen = new HashSet<Guid>();
        var mails = new List<MailCopy>();
        foreach (var candidate in paths)
            foreach (var mail in RowMails(candidate))
                if (seen.Add(mail.UniqueId)) mails.Add(mail);
        return mails;
    }

    private UIDragItem[] DragItems(NSIndexPath path)
    {
        if (_selectionMode && !(_table.IndexPathsForSelectedRows ?? []).Any(candidate => candidate.Section == path.Section && candidate.Row == path.Row)) return [];

        var mails = DraggedMails(path);
        if (mails.Count == 0) return [];
        return [new UIDragItem(new NSItemProvider()) { LocalObject = new MailDragPayload(mails) }];
    }

    private sealed class MailListDragDelegate(MailListPageViewController owner) : UITableViewDragDelegate
    {
        public override UIDragItem[] GetItemsForBeginningDragSession(UITableView tableView, IUIDragSession session, NSIndexPath indexPath)
            => owner.DragItems(indexPath);

        public override bool DragSessionIsRestrictedToDraggingApplication(UITableView tableView, IUIDragSession session) => true;

        public override bool DragSessionAllowsMoveOperation(UITableView tableView, IUIDragSession session) => true;

        public override void DragSessionWillBegin(UITableView tableView, IUIDragSession session)
            => owner.ViewModel.SetDragState(true, MailDragPayload.From(session).Count);

        public override void DragSessionDidEnd(UITableView tableView, IUIDragSession session) => owner.ViewModel.SetDragState(false);

        public override UIDragPreviewParameters? GetDragPreviewParameters(UITableView tableView, NSIndexPath indexPath)
        {
            if (tableView.CellAt(indexPath) is not { } cell) return null;
            var parameters = new UIDragPreviewParameters { BackgroundColor = UIColor.Clear };
            parameters.VisiblePath = UIBezierPath.FromRoundedRect(cell.ContentView.Bounds.Inset(4, 2), 8);
            return parameters;
        }
    }
}

/// <summary>
/// The mails of a drag in progress, carried as the drag item's local object (the Mac MailDragPayload): drags never leave
/// the app, so no pasteboard representation is needed. Folder drop rules match the Mac pane.
/// </summary>
public sealed class MailDragPayload(IReadOnlyList<MailCopy> mails) : NSObject
{
    public IReadOnlyList<MailCopy> Mails { get; } = mails;

#if DEBUG
    /// <summary>The mail list selection, for debug drop commands.</summary>
    public static Func<IReadOnlyList<MailCopy>>? DebugSelection { get; set; }
#endif

    /// <summary>The dragged mails of a local drag session; empty for any other drag.</summary>
    public static IReadOnlyList<MailCopy> From(IUIDragSession? session)
        => session?.Items?.Select(static item => item.LocalObject).OfType<MailDragPayload>().SelectMany(static payload => payload.Mails).ToList() ?? [];

    /// <summary>The dragged mails of a drop session that started in this app; empty otherwise.</summary>
    public static IReadOnlyList<MailCopy> From(IUIDropSession session) => From(session.LocalDragSession);

    /// <summary>True when the folder takes at least one of <paramref name="mails"/> (Windows CanContinueDragDrop).</summary>
    public static bool CanDropOn(Wino.Core.Domain.Interfaces.IBaseFolderMenuItem folder, bool isSelectedFolder, IReadOnlyList<MailCopy> mails)
    {
        if (mails.Count == 0 || isSelectedFolder || !folder.IsMoveTarget) return false;
        var accounts = folder.HandlingFolders.Select(static handling => handling.MailAccountId).ToHashSet();
        return mails.Any(mail => mail.AssignedAccount is { } account && accounts.Contains(account.Id));
    }

    /// <summary>The dragged mails that belong to the folder's accounts (the ones a drop moves).</summary>
    public static List<MailCopy> MailsFor(Wino.Core.Domain.Interfaces.IBaseFolderMenuItem folder, IReadOnlyList<MailCopy> mails)
    {
        var accounts = folder.HandlingFolders.Select(static handling => handling.MailAccountId).ToHashSet();
        return mails.Where(mail => mail.AssignedAccount is { } account && accounts.Contains(account.Id)).ToList();
    }
}
