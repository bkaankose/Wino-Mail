using System.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

internal sealed record MailRowCellContext(IPreferencesService Preferences, LinuxSenderPictureProvider Pictures, Func<bool> IsMergedAccountView, Action<Exception> Error);

/// <summary>
/// A recycled list cell: a group header or a mail row. It maps its row (<see cref="MailRowMapper"/>), follows the row's
/// mails for state that changes without the projection changing (read, flagged), and lets <see cref="WinoMailRow"/> draw.
/// </summary>
internal sealed class MailRowCell : IDisposable
{
    private readonly MailRowCellContext _context;
    private readonly Gtk.Box _root;
    private readonly PropertyChangedEventHandler _leafChanged;
    private readonly PropertyChangedEventHandler _rowChanged;
    private readonly List<INotifyPropertyChanged> _leaves = [];
    private int _pending;
    private int _generation;
    private bool _disposed;

    public MailRowCell(MailRowCellContext context)
    {
        _context = context;
        Row = new WinoMailRow();
        Header = new WinoMailGroupHeader();
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _root.Append(Header.Root);
        _root.Append(Row.Root);
        _leafChanged = LeafChanged;
        _rowChanged = RowChanged;
    }

    public Gtk.Widget Root => _root;

    public WinoMailRow Row { get; }

    public WinoMailGroupHeader Header { get; }

    public MailListRow? Item { get; private set; }

    public void Bind(string? header, MailListRow? row, Gtk.ListItem listItem)
    {
        Unbind();
        if (_disposed) return;
        Header.Root.SetVisible(header is not null);
        Header.Title = header ?? string.Empty;
        Row.Root.SetVisible(row is not null);
        if (row is null)
        {
            listItem.SetAccessibleLabel(header ?? string.Empty);
            return;
        }

        Item = row;
        row.PropertyChanged += _rowChanged;
        SubscribeLeaves(row);
        Refresh();
        listItem.SetAccessibleLabel(Row.Model.AccessibilityText);
    }

    public void Unbind()
    {
        _generation++;
        if (Item is { } row) row.PropertyChanged -= _rowChanged;
        foreach (var leaf in _leaves) leaf.PropertyChanged -= _leafChanged;
        _leaves.Clear();
        Item = null;
    }

    /// <summary>Maps the bound row again now (preferences, picture, mail state).</summary>
    public void Refresh()
    {
        if (_disposed || Item is not { } row) return;
        try
        {
            if (MailRowMapper.Map(row, _context.Preferences, _context.IsMergedAccountView(), _context.Pictures) is { } model) Row.Apply(model);
        }
        catch (Exception exception) { _context.Error(exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unbind();
        _disposed = true;
        Row.Dispose();
    }

    private void SubscribeLeaves(MailListRow row)
    {
        foreach (var leaf in MailRowPresentation.Leaves(row))
        {
            leaf.PropertyChanged += _leafChanged;
            _leaves.Add(leaf);
        }
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
        if (resubscribe) Interlocked.Exchange(ref _pending, 2);
        else if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        var generation = Volatile.Read(ref _generation);
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            var pending = Interlocked.Exchange(ref _pending, 0);
            if (pending == 0 || _disposed || generation != _generation || Item is not { } row) return false;
            if (pending == 2)
            {
                foreach (var leaf in _leaves) leaf.PropertyChanged -= _leafChanged;
                _leaves.Clear();
                SubscribeLeaves(row);
            }
            Refresh();
            return false;
        });
    }
}
