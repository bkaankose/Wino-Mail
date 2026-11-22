using System.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.Mail;

internal sealed record MailRowCellContext(IPreferencesService Preferences, LinuxSenderPictureProvider Pictures, Func<bool> IsMergedAccountView, Action<Exception> Error)
{
    /// <summary>Runs after a cell bound its entry; the view calls its per-cell hooks from here.</summary>
    public Action<MailRowCell, MailListEntry, WinoCellBinding>? Bound { get; init; }

    /// <summary>Runs after a cell mapped its row again (bind, mail state, preferences).</summary>
    public Action<MailRowCell, WinoMailRowModel>? Refreshed { get; init; }
}

/// <summary>
/// A recycled mail list cell. It maps its entry's row (<see cref="MailRowMapper"/>), follows the row's mails for state
/// that changes without the projection changing (read, flagged), and lets <see cref="WinoMailRow"/> draw.
/// </summary>
internal sealed class MailRowCell : IWinoCell<MailListEntry>, IDisposable
{
    private readonly MailRowCellContext _context;
    private readonly Gtk.Overlay _root;
    private readonly PropertyChangedEventHandler _leafChanged;
    private readonly PropertyChangedEventHandler _rowChanged;
    private readonly List<INotifyPropertyChanged> _leaves = [];
    private MailListRow? _subscribedRow;
    private Gtk.ListItem? _listItem;
    private string _listItemLabel = string.Empty;
    private int _pending;
    private int _generation;
    private bool _disposed;

    public MailRowCell(MailRowCellContext context)
    {
        _context = context;
        Row = new WinoMailRow();
        Hover = new WinoHoverActions();
        Swipe = new WinoSwipeRow(Row.Root);
        _root = Gtk.Overlay.New();
        _root.AddCssClass(Wino.Presentation.Linux.Style.WinoCssClass.MailCell);
        _root.SetChild(Swipe.Root);
        _root.AddOverlay(Hover.Root);
        // The bar never sizes the row, and a sliding bar is clipped by it.
        _root.SetMeasureOverlay(Hover.Root, false);
        _root.SetClipOverlay(Hover.Root, true);
        _leafChanged = LeafChanged;
        _rowChanged = RowChanged;
    }

    public Gtk.Widget Root => _root;

    public WinoMailRow Row { get; }

    public WinoHoverActions Hover { get; }

    public WinoSwipeRow Swipe { get; }

    /// <summary>The model the row shows now; null before the first bind.</summary>
    public WinoMailRowModel? Content { get; private set; }

    public MailListEntry? Entry { get; private set; }

    /// <summary>The projection row the cell shows now.</summary>
    public MailListRow? Item => Entry?.Row;

    public void Bind(MailListEntry item, WinoCellBinding binding)
    {
        Unbind();
        if (_disposed) return;
        Entry = item;
        _listItem = binding.ListItem;
        Subscribe(item.Row);
        Refresh();
        _context.Bound?.Invoke(this, item, binding);
    }

    public void Unbind()
    {
        _generation++;
        Unsubscribe();
        Entry = null;
        Content = null;
        _listItem = null;
        _listItemLabel = string.Empty;
        Hover.SetShown(false, animate: false);
        Swipe.Cancel();
    }

    /// <summary>Follows the entry's current row again after the view replaced it with an equal-key row.</summary>
    public void Rebind()
    {
        if (_disposed || Entry is not { } entry) return;
        Unsubscribe();
        Subscribe(entry.Row);
        Refresh();
    }

    /// <summary>Maps the bound row again now (preferences, picture, mail state).</summary>
    public void Refresh()
    {
        if (_disposed || Entry is not { } entry) return;
        try
        {
            if (MailRowMapper.Map(entry.Row, _context.Preferences, _context.IsMergedAccountView(), _context.Pictures) is not { } model) return;
            Row.Apply(model);
            Content = model;
            if (_listItem is { } listItem && !string.Equals(_listItemLabel, model.AccessibilityText, StringComparison.Ordinal))
            {
                _listItemLabel = model.AccessibilityText;
                listItem.SetAccessibleLabel(model.AccessibilityText);
            }
            _context.Refreshed?.Invoke(this, model);
        }
        catch (Exception exception) { _context.Error(exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unbind();
        _disposed = true;
        Hover.Dispose();
        Swipe.Dispose();
        Row.Dispose();
    }

    private void Subscribe(MailListRow row)
    {
        _subscribedRow = row;
        row.PropertyChanged += _rowChanged;
        foreach (var leaf in MailRowPresentation.Leaves(row))
        {
            leaf.PropertyChanged += _leafChanged;
            _leaves.Add(leaf);
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedRow is { } row) row.PropertyChanged -= _rowChanged;
        _subscribedRow = null;
        foreach (var leaf in _leaves) leaf.PropertyChanged -= _leafChanged;
        _leaves.Clear();
    }

    // Mail items can change on a worker thread: only a flag and a thread-safe idle source here.
    private void LeafChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MailItemViewModel.IsSelected) or nameof(MailItemViewModel.ThumbnailUpdatedEvent)) return;
        RequestRefresh(false);
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs args)
        => RequestRefresh(args.PropertyName is nameof(MailListRow.Thread) or nameof(MailListRow.LeafItems));

    private void RequestRefresh(bool resubscribe)
    {
        if (resubscribe)
        {
            if (Interlocked.Exchange(ref _pending, 2) != 0) return;
        }
        else if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        var generation = Volatile.Read(ref _generation);
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            var pending = Interlocked.Exchange(ref _pending, 0);
            if (pending == 0 || _disposed || generation != _generation || Entry is null) return false;
            if (pending == 2) Rebind();
            else Refresh();
            return false;
        });
    }
}
