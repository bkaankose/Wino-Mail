using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Keyboard movement for the shell: where focus lands in the list (F6, Escape), Enter into the reader, Alt+Down and
/// Alt+Up (and j, k) through the messages with the reader following, and Ctrl+J for junk.
/// </summary>
internal sealed partial class MailListView : IPaneFocus, IItemNavigation, IKeyActionTarget
{
    partial void SetUpNavigation()
    {
        var self = _self;
        var list = List;
        Bindings.Own(GtkSignals.Connect<Gtk.ListView, Gtk.ListView.ActivateSignalArgs>(
            h => list.OnActivate += h, h => list.OnActivate -= h, (_, _) => self.Value?.RowActivated()));
    }

    public bool FocusPrimary()
    {
        if (IsReleased || !List.GetMapped()) return false;
        var selected = ListViews.SelectedPositions(Selection);
        uint? target = _active is { } active && PositionOf(active) is { } position ? position
            : selected.Length > 0 ? selected[0]
            : RowCount > 0 ? 0 : null;
        if (target is { } row && FocusRow(row)) return true;
        return List.GrabFocus();
    }

    public bool MoveItem(int delta)
    {
        if (IsReleased || RowCount == 0) return false;
        var hadFocus = HasListFocus();
        var selected = ListViews.SelectedPositions(Selection);
        long start = selected.Length == 1 ? selected[0]
            : _active is { } active && PositionOf(active) is { } activePosition ? activePosition
            : FocusedPosition() is { } focused ? focused
            : delta > 0 ? -1 : RowCount;
        var target = start + delta;
        if (target < 0 || target >= RowCount || EntryAt((uint)target) is not { } entry) return true;
        SelectEntries([entry], scrollToFirst: true);
        if (hadFocus) FocusRow((uint)target);
        return true;
    }

    public bool TryHandleKeyAction(string action)
    {
        if (action != LinuxKeymap.Junk || IsReleased || ViewModel.SelectedItemsCount == 0) return false;
        ViewModel.ExecuteMailOperationCommand.Execute(ViewModel.IsJunkFolder ? MailOperation.MarkAsNotJunk : MailOperation.MoveToJunk);
        return true;
    }

    /// <summary>Enter (or a double click) on a row: the reader or composer of that row takes the focus once it shows.</summary>
    private void RowActivated()
    {
        if (!IsReleased) _navigation.Shell?.RequestDetailFocus();
    }

#if DEBUG
    internal string DebugSelectedPositions => string.Join(',', ListViews.SelectedPositions(Selection).Take(5));

    /// <summary>The list's own keys (Escape, Delete, Right), which AT-SPI cannot type.</summary>
    internal bool DebugKey(uint keyval, Gdk.ModifierType state) => HandleListKey(keyval, state);
#endif

    private bool FocusRow(uint position)
    {
        List.ScrollTo(position, Gtk.ListScrollFlags.Focus, null);
        return HasListFocus();
    }

    private bool HasListFocus() => List.GetRoot() is Gtk.Window window && ShellView.Contains(List, window.GetFocus());

    private uint? FocusedPosition()
    {
        MailListEntry? focused = null;
        Cells.ForEachBound((cell, entry) =>
        {
            if (cell.Root.GetParent() is { HasFocus: true }) focused = entry;
        });
        return focused is null ? null : PositionOf(focused);
    }
}
