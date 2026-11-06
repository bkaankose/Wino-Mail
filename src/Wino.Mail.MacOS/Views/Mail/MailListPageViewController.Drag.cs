using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Mail list drag source (Windows ThreadedMailListView.OnDragItemsStarting / OnDragItemsCompleted):
/// rows write <see cref="MailDragPayload.PasteboardType"/> items, the session shows one stacked card
/// with a count badge, dragged rows dim, and the ViewModel drag state drives the "Dragging N item(s)"
/// banner over the bottom of the list. Drops are handled by the shell pane folders.
/// </summary>
public sealed partial class MailListPageViewController
{
    private readonly List<NSTableRowView> _dimmedRows = new();
    private WinoSurfaceView _dragBanner = null!;
    private NSTextField _dragBannerText = null!;

    /// <summary>Adds the drag banner over the list (Windows DraggingMessageBorder: bottom right, card fill, 8pt corners).</summary>
    private void BuildDragBanner(NSView listHost)
    {
        _dragBannerText = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.PrimaryText);
        _dragBanner = new WinoSurfaceView { Fill = WinoStyle.GroupFill, Stroke = WinoStyle.ZoneStroke, StrokeWidth = 1, CornerRadius = 8, Hidden = true };
        _dragBanner.Shadow = new NSShadow { ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.16f), ShadowBlurRadius = 8, ShadowOffset = new CGSize(0, -2) };
        WinoLayout.Fill(_dragBannerText, _dragBanner, 6, 10, 6, 10);
        listHost.AddSubview(_dragBanner, NSWindowOrderingMode.Above, null);
        NSLayoutConstraint.ActivateConstraints(
        [
            _dragBanner.TrailingAnchor.ConstraintEqualTo(listHost.TrailingAnchor, -14),
            _dragBanner.BottomAnchor.ConstraintEqualTo(listHost.BottomAnchor, -14)
        ]);

        // Internal moves only: dragging mails out of the app is not supported.
        _table.SetDraggingSourceOperationMask(NSDragOperation.Move, true);
        _table.SetDraggingSourceOperationMask(NSDragOperation.None, false);
    }

    private void BindDrag()
    {
        Bind(nameof(ViewModel.IsDragInProgress), vm => vm.IsDragInProgress, _ => UpdateDragBanner());
        Bind(nameof(ViewModel.DraggingItemsCount), vm => vm.DraggingItemsCount, _ => UpdateDragBanner());
#if DEBUG
        MailDragPayload.DebugSelection = () => _released ? [] : ViewModel.SelectedItems.Select(static item => item.MailCopy).ToList();
        // "drag-banner N|off" shows the list's drag banner; "drag-image [CAPTION…]" writes the drag
        // image for the current selection (first selected row snapshot) to the wino-debug folder.
        Infrastructure.MacDebugBridge.Register("drag-banner", args =>
        {
            bool off = args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            ViewModel.SetDragState(!off, off ? 0 : args.Length > 0 && int.TryParse(args[0], out var count) ? count : Math.Max(1, ViewModel.SelectedItemsCount));
            return Task.FromResult(off ? "off" : ViewModel.DraggingMessageText);
        });
        Infrastructure.MacDebugBridge.Register("drag-image", args =>
        {
            var mails = DraggedMails(_table.SelectedRows);
            if (mails.Count == 0) return Task.FromResult("no selection");
            MailDragPayload.Begin(mails, SnapshotRow((nint)_table.SelectedRows.FirstIndex));
            var caption = args.Length > 0 ? string.Join(' ', args) : null;
            var image = MailDragPayload.CreateImage(mails.Count, caption, caption == Translator.DragCannotMoveHereCaption);
            MailDragPayload.End();
            var file = Path.Combine(Path.GetTempPath(), "wino-debug", "drag-image.png");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var tiff = image.AsTiff();
            using var rep = tiff is null ? null : new NSBitmapImageRep(tiff);
            using var png = rep?.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            png?.Save(file, true);
            return Task.FromResult($"{mails.Count} mails -> {file}");
        });
#endif
    }

    private void UpdateDragBanner()
    {
        _dragBannerText.StringValue = ViewModel.DraggingMessageText;
        _dragBanner.Hidden = !ViewModel.IsDragInProgress;
    }

    /// <summary>The mails a row stands for: a thread head drags every mail of its thread.</summary>
    private IEnumerable<MailCopy> RowMails(nint index)
    {
        if (RowAt(index) is not { } row) yield break;
        var leaves = row.IsThreadHead ? row.LeafItems : [row.SourceItem];
        foreach (var leaf in leaves.OfType<MailItemViewModel>()) yield return leaf.MailCopy;
    }

    private List<MailCopy> DraggedMails(NSIndexSet rows)
    {
        var seen = new HashSet<Guid>();
        var mails = new List<MailCopy>();
        foreach (var index in rows.ToArray())
            foreach (var mail in RowMails((nint)index))
                if (seen.Add(mail.UniqueId)) mails.Add(mail);
        return mails;
    }

    private INSPasteboardWriting? PasteboardWriterForRow(nint row)
    {
        var mails = RowMails(row).ToList();
        return mails.Count == 0 ? null : MailDragPayload.CreatePasteboardItem(mails);
    }

    private void DragWillBegin(NSDraggingSession session, CGPoint screenPoint, NSIndexSet rows)
    {
        var mails = DraggedMails(rows);
        if (mails.Count == 0) return;

        MailDragPayload.Begin(mails, SnapshotRow((nint)rows.FirstIndex));
        var image = MailDragPayload.CreateImage(mails.Count);
        var window = _table.Window;
        var pointer = window is null ? CGPoint.Empty : _table.ConvertPointFromView(window.ConvertPointFromScreen(screenPoint), null);
        var frame = new CGRect(pointer.X - 28, pointer.Y - 24, image.Size.Width, image.Size.Height);
        var empty = new NSImage(new CGSize(1, 1));

        // One card stands for the whole selection; the per-row items stay invisible.
        session.DraggingFormation = NSDraggingFormation.None;
        session.AnimatesToStartingPositionsOnCancelOrFail = true;
        session.EnumerateDraggingItems(NSDraggingItemEnumerationOptions.Concurrent, _table, MailDragPayload.ItemClasses(), new NSDictionary(),
            (NSDraggingItem item, nint index, ref bool stop) =>
            {
                if (index == 0) item.SetDraggingFrame(frame, image);
                else item.SetDraggingFrame(new CGRect(frame.Location, new CGSize(1, 1)), empty);
            });

        foreach (var index in rows.ToArray())
        {
            if (_table.GetRowView((nint)index, false) is not { } rowView) continue;
            rowView.AlphaValue = 0.45f;
            _dimmedRows.Add(rowView);
        }
        ViewModel.SetDragState(true, mails.Count);
    }

    private void DragEnded()
    {
        MailDragPayload.End();
        foreach (var rowView in _dimmedRows) rowView.AlphaValue = 1;
        _dimmedRows.Clear();
        ViewModel.SetDragState(false);
    }

    private NSImage? SnapshotRow(nint row)
    {
        if (row < 0 || _table.GetRowView(row, false) is not { } rowView) return null;
        var bounds = rowView.Bounds;
        var rep = rowView.BitmapImageRepForCachingDisplayInRect(bounds);
        if (rep is null) return null;
        rowView.CacheDisplay(bounds, rep);
        var image = new NSImage(bounds.Size);
        image.AddRepresentation(rep);
        return image;
    }
}
