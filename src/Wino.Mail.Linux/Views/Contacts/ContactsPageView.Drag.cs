using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// Dragging contacts onto a contact list in the sidebar (Windows ContactsListView_DragItemsStarting, macOS
/// PasteboardWriterForRow): a pointer drag source per cell; a row inside the multi-selection drags the selection. The
/// sidebar's drop handler adds them (copy, never move). A touch drag never starts.
/// </summary>
internal sealed partial class ContactsPageView
{
    private readonly Dictionary<ContactCell, IDisposable> _dragCells = new(ReferenceEqualityComparer.Instance);
    private bool _dragReleaseOwned;

    partial void DragCellSetup(ContactCell cell)
    {
        if (!_dragReleaseOwned)
        {
            _dragReleaseOwned = true;
            var owner = _self;
            Bindings.Own(new ActionDisposable(() =>
            {
                ContactDragPayload.End();
                if (owner.Value is not { } view) return;
                foreach (var subscription in view._dragCells.Values) subscription.Dispose();
                view._dragCells.Clear();
            }));
        }
        var self = _self;
        var root = cell.Root;
        var source = Gtk.DragSource.New();
        source.SetActions(Gdk.DragAction.Copy);
        var prepare = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.PrepareSignalArgs, Gdk.ContentProvider?>(
            h => source.OnPrepare += h, h => source.OnPrepare -= h, (sender, _) =>
            {
                if (self.Value is not { IsReleased: false } view || sender.GetCurrentEventDevice()?.GetSource() == Gdk.InputSource.Touchscreen) return null;
                return cell.Item is { } contact && view.BeginDrag(contact) is { } token ? Gdk.ContentProvider.NewForValue(new GObject.Value(token)) : null;
            }, null);
        var begin = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragBeginSignalArgs>(
            h => source.OnDragBegin += h, h => source.OnDragBegin -= h, (_, args) =>
            {
                if (self.Value is not { } view || ContactDragPayload.Current.Count == 0) return;
                var card = view.CreateDragCard(cell, ContactDragPayload.Current.Count);
                Gtk.DragIcon.GetForDrag(args.Drag).SetChild(card.Root);
                ContactDragPayload.SetCard(card);
            });
        var end = GtkSignals.Connect<Gtk.DragSource, Gtk.DragSource.DragEndSignalArgs>(
            h => source.OnDragEnd += h, h => source.OnDragEnd -= h, (_, _) => ContactDragPayload.End());
        var cancel = GtkSignals.ConnectReturning<Gtk.DragSource, Gtk.DragSource.DragCancelSignalArgs, bool>(
            h => source.OnDragCancel += h, h => source.OnDragCancel -= h, (_, _) =>
            {
                ContactDragPayload.End();
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

    partial void DragCellTeardown(ContactCell cell)
    {
        if (_dragCells.Remove(cell, out var subscription)) subscription.Dispose();
    }

    /// <summary>Starts the drag of <paramref name="contact"/> (or the selection it belongs to) and returns its token.</summary>
    internal string? BeginDrag(AccountContactViewModel contact)
    {
        Guid[] ids = [.. ViewModel.ResolveContactDragIds([contact])];
        return ids.Length == 0 ? null : ContactDragPayload.Begin(ids, null);
    }

    internal MailDragCard CreateDragCard(ContactCell cell, int count)
    {
        Gdk.Paintable? image = null;
        try { image = Gtk.WidgetPaintable.New(cell.Row.Root).GetCurrentImage(); }
        catch (Exception exception) { ReportError(exception); }
        return new MailDragCard(count, image, cell.Item?.Name ?? string.Empty);
    }
}
