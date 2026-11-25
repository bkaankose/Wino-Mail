using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Dragging mail rows onto sidebar folders (macOS <c>MailListPageViewController.Drag</c>): a pointer drag source per cell,
/// the drag card, dimmed source rows and the "Dragging N item(s)" banner. The sidebar's drop target performs the move.
/// A touch drag never starts a mail drag.
/// </summary>
internal sealed partial class MailListView
{
    private readonly Dictionary<MailRowCell, IDisposable> _dragCells = new(ReferenceEqualityComparer.Instance);
    private Gtk.Label? _dragBanner;

    partial void SetUpDrag()
    {
        var banner = Gtk.Label.New(string.Empty);
        banner.AddCssClass(WinoCssClass.DragBanner);
        banner.SetHalign(Gtk.Align.End);
        banner.SetValign(Gtk.Align.End);
        banner.SetCanTarget(false);
        banner.SetVisible(false);
        FloatingSlot.Append(banner);
        _dragBanner = banner;
        Bind.Bind(nameof(MailListPageViewModel.IsDragInProgress), static vm => vm.IsDragInProgress, banner.SetVisible);
        Bind.Bind(nameof(MailListPageViewModel.DraggingMessageText), static vm => vm.DraggingMessageText, text => banner.SetText(text ?? string.Empty));

        var self = _self;
        Bindings.Own(new ActionDisposable(() =>
        {
            if (self.Value is not { } view) return;
            foreach (var subscription in view._dragCells.Values) subscription.Dispose();
            view._dragCells.Clear();
        }));
    }

    partial void DragCellSetup(MailRowCell cell)
    {
        var self = _self;
        var root = cell.Root;
        var source = Gtk.DragSource.New();
        source.SetActions(Gdk.DragAction.Move);
        var prepare = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
            h => source.OnPrepare += h, h => source.OnPrepare -= h, (sender, _) =>
            {
                if (self.Value is not { IsReleased: false } view || sender.GetCurrentEventDevice()?.GetSource() == Gdk.InputSource.Touchscreen) return null;
                return cell.Item is { } row && view.BeginDrag(row) is { } token ? Gdk.ContentProvider.NewForValue(new GObject.Value(token)) : null;
            }, null);
        var begin = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragBeginSignalArgs>(
            h => source.OnDragBegin += h, h => source.OnDragBegin -= h, (_, args) =>
            {
                if (self.Value is not { } view || MailDragPayload.Current.Count == 0) return;
                var card = view.CreateDragCard(cell, MailDragPayload.Current.Count);
                Gtk.DragIcon.GetForDrag(args.Drag).SetChild(card.Root);
                MailDragPayload.SetCard(card);
            });
        var end = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragEndSignalArgs>(
            h => source.OnDragEnd += h, h => source.OnDragEnd -= h, (_, _) => MailDragPayload.End());
        var cancel = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.DragCancelSignalArgs, bool>(
            h => source.OnDragCancel += h, h => source.OnDragCancel -= h, (_, _) =>
            {
                MailDragPayload.End();
                return false;
            }, false);
        root.AddController(source);
        _dragCells[cell] = new ActionDisposable(() =>
        {
            prepare.Dispose();
            begin.Dispose();
            end.Dispose();
            cancel.Dispose();
            root.RemoveController(source);
        });
    }

    partial void DragCellBound(MailRowCell cell, MailListRow row) => ApplyDim(cell, row, DraggedIds());

    partial void DragCellTeardown(MailRowCell cell)
    {
        if (_dragCells.Remove(cell, out var subscription)) subscription.Dispose();
        cell.Root.RemoveCssClass(WinoCssClass.Dragging);
    }

    /// <summary>Starts the mail drag of <paramref name="row"/>'s targets and returns its token; null refuses the drag.</summary>
    private string? BeginDrag(MailListRow row)
    {
        var mails = MailListTargets.For(row, ViewModel).Mails.DistinctBy(static mail => mail.UniqueId).ToArray();
        if (mails.Length == 0) return null;
        var self = _self;
        var token = MailDragPayload.Begin(mails, () => self.Value?.DragEnded());
        ViewModel.SetDragState(true, mails.Length);
        ApplyDim();
        return token;
    }

    private MailDragCard CreateDragCard(MailRowCell cell, int count)
    {
        Gdk.Paintable? image = null;
        try { image = Gtk.WidgetPaintable.New(cell.Row.Root).GetCurrentImage(); }
        catch (Exception exception) { ReportError(exception); }
        var title = cell.Content is { } content ? (string.IsNullOrWhiteSpace(content.Subject) ? content.Sender : content.Subject) : string.Empty;
        return new MailDragCard(count, image, title);
    }

    private void DragEnded()
    {
        if (IsReleased) return;
        ViewModel.SetDragState(false);
        ApplyDim();
    }

    private HashSet<Guid>? DraggedIds()
    {
        var current = MailDragPayload.Current;
        if (current.Count == 0) return null;
        var ids = new HashSet<Guid>();
        foreach (var mail in current) ids.Add(mail.UniqueId);
        return ids;
    }

    private void ApplyDim()
    {
        var ids = DraggedIds();
        Cells.ForEachBound((cell, entry) => ApplyDim(cell, entry.Row, ids));
    }

    private static void ApplyDim(MailRowCell cell, MailListRow row, HashSet<Guid>? ids)
    {
        var dim = ids is not null && MailRowPresentation.Leaves(row).Any(leaf => ids.Contains(leaf.MailCopy.UniqueId));
        if (dim) cell.Root.AddCssClass(WinoCssClass.Dragging);
        else cell.Root.RemoveCssClass(WinoCssClass.Dragging);
    }

#if DEBUG
    internal int DebugDimmedCount()
    {
        var count = 0;
        Cells.ForEachBound((cell, _) =>
        {
            if (cell.Root.HasCssClass(WinoCssClass.Dragging)) count++;
        });
        return count;
    }

    internal bool DebugBannerVisible => _dragBanner?.GetVisible() == true;

    internal string DebugBannerText => _dragBanner?.GetText() ?? string.Empty;

    /// <summary>Begins a drag of the first selected row's targets without a pointer, through the drag source's own path.</summary>
    internal string? DebugBeginDrag()
    {
        foreach (var entry in _shown)
        {
            if (entry.Row.SourceItem is MailItemViewModel item && ViewModel.IsMailSelected(item.UniqueId)) return BeginDrag(entry.Row);
        }
        return null;
    }

    internal MailDragCard? DebugCreateCard(int count)
    {
        foreach (var entry in _shown)
        {
            if (entry.Row.SourceItem is MailItemViewModel item && ViewModel.IsMailSelected(item.UniqueId))
                return Cells.CellOf(entry) is { } cell ? CreateDragCard(cell, count) : null;
        }
        return null;
    }
#endif
}
