using System.Diagnostics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// A search field over an account's folder tree, fully expanded (Windows <c>MoveMailDialog</c>, macOS
/// <c>MoveFolderPopover</c>). Typing filters to the matching folders that can be picked, as a flat list; Return in
/// the field picks the first. A folder that is not a move target, or is a source folder, explains itself instead.
/// Rows are buttons so the keyboard and accessibility clients can click them; a filter only hides rows.
/// </summary>
internal sealed class FolderPicker : IDisposable
{
    private const int Indentation = 14;

    private readonly Gtk.Box _root;
    private readonly Gtk.SearchEntry _search;
    private readonly Gtk.Label _warning;
    private readonly Gtk.Box _list;
    private readonly List<Row> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    /// <param name="excluded">Folders that cannot be picked although they are move targets (the source folders).</param>
    public FolderPicker(IReadOnlyList<IMailItemFolder?> roots, IReadOnlySet<Guid>? excluded = null)
    {
        WinoLiveObjects.Track(this);
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 8);

        _search = Gtk.SearchEntry.New();
        _search.SetPlaceholderText(Translator.ContextFlyout_SearchPlaceholder);
        _search.SetSearchDelay(0);
        WinoA11y.Label(_search, Translator.ContextFlyout_SearchPlaceholder);
        _root.Append(_search);

        _warning = Gtk.Label.New(string.Empty);
        _warning.AddCssClass("warning");
        _warning.AddCssClass("caption");
        _warning.SetWrap(true);
        _warning.SetXalign(0);
        _warning.SetVisible(false);
        _root.Append(_warning);

        _list = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _list.AddCssClass(WinoCssClass.FolderPicker);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetMinContentHeight(240);
        scroll.SetMaxContentHeight(420);
        scroll.SetPropagateNaturalHeight(true);
        scroll.SetChild(_list);
        _root.Append(scroll);

        var started = Stopwatch.GetTimestamp();
        AddRows(roots, 0, excluded);
        Serilog.Log.Debug("Folder picker built {Rows} rows in {Milliseconds} ms", _rows.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var search = _search;
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnSearchChanged += h, h => search.OnSearchChanged -= h, (_, _) => Filter(search.GetText())));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnActivate += h, h => search.OnActivate -= h, (_, _) => PickFirstVisible()));
        // Escape with text clears it; with an empty field it is left to the dialog or popover, which closes.
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnStopSearch += h, h => search.OnStopSearch -= h, (_, _) =>
        {
            if (search.GetText().Length > 0) search.SetText(string.Empty);
        }));
#if DEBUG
        DebugOpened?.Invoke(this);
#endif
    }

    public Gtk.Widget Root => _root;

    public event Action<IMailItemFolder>? Picked;

    /// <summary>Rows shown now.</summary>
    public int VisibleCount => _rows.Count(static row => row.Button.GetVisible());

    public void FocusSearch() => _search.GrabFocus();

    public void SetFilter(string text) => _search.SetText(text);

    /// <summary>Return in the search field: picks the first shown pickable row.</summary>
    public bool PickFirstVisible()
    {
        foreach (var row in _rows)
        {
            if (!row.Button.GetVisible() || !row.Pickable) continue;
            Picked?.Invoke(row.Folder);
            return true;
        }
        return false;
    }

    /// <summary>The click path of the first shown row with that name.</summary>
    public bool PickByName(string displayName)
    {
        foreach (var row in _rows)
        {
            if (!row.Button.GetVisible() || row.Name != displayName) continue;
            Click(row);
            return true;
        }
        return false;
    }

    public string WarningText => _warning.GetVisible() ? _warning.GetText() : string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Picked = null;
#if DEBUG
        DebugOpened?.Invoke(null);
#endif
    }

#if DEBUG
    internal static event Action<FolderPicker?>? DebugOpened;
#endif

    /// <summary>Depth first; a folder without a name contributes its children at its own depth (Windows <c>AddMoveFolderItems</c>).</summary>
    private void AddRows(IEnumerable<IMailItemFolder?> folders, int depth, IReadOnlySet<Guid>? excluded)
    {
        foreach (var folder in folders)
        {
            if (folder is null) continue;
            var name = folder.ResolvedDisplayName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (folder.ChildFolders is { } unnamed) AddRows(unnamed, depth, excluded);
                continue;
            }
            var pickable = folder.IsMoveTarget && excluded?.Contains(folder.Id) != true;
            _rows.Add(CreateRow(folder, name, depth, pickable));
            if (folder.ChildFolders is { } children) AddRows(children, depth + 1, excluded);
        }
    }

    private Row CreateRow(IMailItemFolder folder, string name, int depth, bool pickable)
    {
        var button = Gtk.Button.New();
        button.AddCssClass("flat");
        button.AddCssClass(WinoCssClass.FolderPickerRow);
        button.SetHalign(Gtk.Align.Fill);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var glyph = SpecialFolderGlyphs.GetGlyph(folder.SpecialFolderType);
        content.Append(WinoIcons.Label(glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph, 16));
        var label = Gtk.Label.New(name);
        label.SetXalign(0);
        label.SetHexpand(true);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        if (WinoTint.ClassFor(folder.TextColorHex) is { } tint) label.AddCssClass(tint);
        content.Append(label);
        if (!pickable) content.AddCssClass("dim-label");
        button.SetChild(content);
        WinoA11y.Rename(button, name);
        var row = new Row(folder, name, depth, pickable, button, content);
        content.SetMarginStart(depth * Indentation);
        _subscriptions.Add(GtkSignals.OnClicked(button, () => Click(row)));
        _list.Append(button);
        return row;
    }

    private void Click(Row row)
    {
        if (row.Pickable)
        {
            Picked?.Invoke(row.Folder);
            return;
        }
        _warning.SetText(string.Format(Translator.MoveMailDialog_InvalidFolderMessage, row.Name));
        _warning.SetVisible(true);
    }

    private void Filter(string text)
    {
        _warning.SetVisible(false);
        var query = text.Trim();
        foreach (var row in _rows)
        {
            var visible = query.Length == 0 || (row.Pickable && row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            row.Button.SetVisible(visible);
            row.Content.SetMarginStart(query.Length == 0 ? row.Depth * Indentation : 0);
        }
    }

    private sealed record Row(IMailItemFolder Folder, string Name, int Depth, bool Pickable, Gtk.Button Button, Gtk.Box Content);
}

/// <summary>The move dialog (Windows <c>MoveMailDialog</c>): the picker in an <see cref="Adw.Dialog"/>; closing it answers null.</summary>
internal static class FolderPickerDialog
{
    public static Task<IMailItemFolder?> PresentAsync(Gtk.Widget owner, IReadOnlyList<IMailItemFolder?> folders, IReadOnlySet<Guid>? excluded)
    {
        var completion = new TaskCompletionSource<IMailItemFolder?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new FolderPicker(folders, excluded);
        var dialog = Adw.Dialog.New();
        dialog.SetTitle(Translator.MoveMailDialog_Title);
        dialog.SetContentWidth(420);
        dialog.SetFollowsContentSize(true);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        var content = picker.Root;
        content.SetMarginStart(12);
        content.SetMarginEnd(12);
        content.SetMarginBottom(12);
        toolbar.SetContent(content);
        dialog.SetChild(toolbar);

        IMailItemFolder? chosen = null;
        picker.Picked += folder =>
        {
            chosen = folder;
            dialog.Close();
        };
        IDisposable? closed = null;
        closed = GtkSignals.Connect<Adw.Dialog>(h => dialog.OnClosed += h, h => dialog.OnClosed -= h, (_, _) =>
        {
            closed?.Dispose();
            picker.Dispose();
            completion.TrySetResult(chosen);
        });
        dialog.Present(owner);
        picker.FocusSearch();
        return completion.Task;
    }
}
