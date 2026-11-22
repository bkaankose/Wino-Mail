using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>Keyboard movement for the shell: the list and the contact detail as panes, Enter into the detail, Alt+Down and Alt+Up through the contacts.</summary>
internal sealed partial class ContactsPageView : IPaneFocus, IDetailPane, IItemNavigation
{
    private Action? _detailRequested;
    private bool _activateConnected;

    public bool HasDetail => ViewModel.SelectedContact is not null && _layout.ShowsEnd;

    public Gtk.Widget? DetailRoot => _detailHost;

    public Action? DetailRequested
    {
        set
        {
            _detailRequested = value;
            if (_activateConnected || IsReleased) return;
            _activateConnected = true;
            var self = _self;
            var list = _list;
            Bindings.Own(GtkSignals.Connect<Gtk.ListView, Gtk.ListView.ActivateSignalArgs>(
                h => list.OnActivate += h, h => list.OnActivate -= h, (_, _) => self.Value?._detailRequested?.Invoke()));
        }
    }

    public bool FocusPrimary()
    {
        if (IsReleased || !_list.GetMapped()) return false;
        var selected = ListViews.SelectedPositions(_selection);
        if (selected.Length > 0 || RowCount > 0)
        {
            _list.ScrollTo(selected.Length > 0 ? selected[0] : 0, Gtk.ListScrollFlags.Focus, null);
            if (_list.GetRoot() is Gtk.Window window && ShellView.Contains(_list, window.GetFocus())) return true;
        }
        return _list.GrabFocus();
    }

    public bool FocusDetail() => !IsReleased && _detailHost.GetMapped() && _detailHost.ChildFocus(Gtk.DirectionType.TabForward);

    public bool CloseDetail()
    {
        if (IsReleased || _layout.State != EndPaneState.Swapped) return false;
        ViewModel.ClearSelectedContactCommand.Execute(null);
        return true;
    }

    public bool MoveItem(int delta)
    {
        if (IsReleased || RowCount == 0) return false;
        long start = ViewModel.SelectedContact is { } current && PositionOf(current) is { } position ? position : delta > 0 ? -1 : RowCount;
        var target = start + delta;
        if (target < 0 || target >= RowCount || ItemAtPosition((uint)target) is not { } next) return true;
        ViewModel.SelectedContact = next;
        return true;
    }
}
