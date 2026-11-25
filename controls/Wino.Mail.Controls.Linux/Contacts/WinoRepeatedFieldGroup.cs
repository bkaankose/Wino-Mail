using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Contacts;

/// <summary>
/// A preferences group whose rows the page adds and removes (e-mail addresses, phones, relations), with an optional
/// "add" row at the end. Rows keep their widgets while they stay, so adding a row never moves the focus.
/// </summary>
public sealed class WinoRepeatedFieldGroup : IDisposable
{
    private readonly Adw.PreferencesGroup _group = Adw.PreferencesGroup.New();
    private readonly List<Gtk.Widget> _rows = [];
    private readonly Adw.ButtonRow? _add;
    private readonly IDisposable? _addActivated;
    private bool _canAdd = true;

    public WinoRepeatedFieldGroup(string title, string? description, string? addLabel)
    {
        _group.SetTitle(GLib.Functions.MarkupEscapeText(title, -1));
        if (!string.IsNullOrEmpty(description)) _group.SetDescription(GLib.Functions.MarkupEscapeText(description, -1));
        if (string.IsNullOrEmpty(addLabel)) return;
        var add = Adw.ButtonRow.New();
        add.SetUseMarkup(false);
        add.SetTitle(addLabel);
        add.SetStartIconName("list-add-symbolic");
        _add = add;
        _addActivated = GtkSignals.Connect<Adw.ButtonRow>(h => add.OnActivated += h, h => add.OnActivated -= h, (_, _) => AddRequested?.Invoke());
        _group.Add(add);
    }

    public Gtk.Widget Root => _group;

    public Adw.PreferencesGroup Group => _group;

    public int Count => _rows.Count;

    public event Action? AddRequested;

    /// <summary>False hides the add row (at most three e-mail addresses).</summary>
    public bool CanAdd
    {
        get => _canAdd;
        set
        {
            if (_canAdd == value) return;
            _canAdd = value;
            _add?.SetVisible(value);
        }
    }

    /// <summary>Inserts a row at <paramref name="index"/> among the page's rows, before the add row.</summary>
    public void Insert(Gtk.Widget row, int index)
    {
        index = Math.Clamp(index, 0, _rows.Count);
        // A preferences group only appends: the rows after the insertion point and the add row are appended again.
        var tail = _rows.GetRange(index, _rows.Count - index);
        foreach (var moved in tail) _group.Remove(moved);
        if (_add is not null) _group.Remove(_add);
        _rows.Insert(index, row);
        _group.Add(row);
        foreach (var moved in tail) _group.Add(moved);
        if (_add is not null) _group.Add(_add);
    }

    public void Remove(Gtk.Widget row)
    {
        if (!_rows.Remove(row)) return;
        _group.Remove(row);
    }

    public void Dispose()
    {
        _addActivated?.Dispose();
        AddRequested = null;
    }
}
