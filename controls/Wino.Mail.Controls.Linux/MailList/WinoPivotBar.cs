using System.Globalization;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>A pivot of the mail list header; a <see cref="Count"/> of 0 shows no count.</summary>
public readonly record struct WinoPivotItem(string Title, int Count)
{
    public string Text => Count > 0 ? string.Create(CultureInfo.CurrentCulture, $"{Title} ({Count})") : Title;
}

/// <summary>
/// The folder pivots of the mail list header: one pivot is a heading, two or more are flat toggle buttons in one radio
/// group with an accent underline on the active one.
/// </summary>
public sealed class WinoPivotBar : IDisposable
{
    private readonly Gtk.Box _root;
    private readonly List<(Gtk.Widget Widget, IDisposable? Toggled)> _children = [];
    private WinoPivotItem[] _items = [];
    private int _selectedIndex = -1;
    private bool _applying;
    private bool _disposed;

    public WinoPivotBar()
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _root.AddCssClass(WinoCssClass.PivotBar);
        _root.SetHexpand(true);
        WinoLiveObjects.Track(this);
    }

    public Gtk.Widget Root => _root;

    /// <summary>The user chose another pivot.</summary>
    public event Action<int>? SelectionChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            ApplySelection();
        }
    }

    public int Count => _items.Length;

    public void SetItems(WinoPivotItem[] items, int selectedIndex)
    {
        if (_disposed) return;
        var sameTitles = items.Length == _items.Length;
        for (var index = 0; sameTitles && index < items.Length; index++) sameTitles = items[index].Title == _items[index].Title;
        _items = items;
        _selectedIndex = selectedIndex;
        if (!sameTitles) Rebuild();
        else
        {
            for (var index = 0; index < items.Length; index++) SetText(_children[index].Widget, items[index].Text);
        }
        ApplySelection();
    }

    /// <summary><c>title (count)*</c> per pivot, the active one marked with <c>*</c>.</summary>
    public string Describe()
        => string.Join(", ", _items.Select((item, index) => item.Text + (index == _selectedIndex ? "*" : string.Empty)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        SelectionChanged = null;
    }

    private void Rebuild()
    {
        Clear();
        if (_items.Length == 1)
        {
            var label = Gtk.Label.New(_items[0].Text);
            label.AddCssClass("heading");
            label.SetXalign(0);
            label.SetEllipsize(Pango.EllipsizeMode.End);
            label.SetMaxWidthChars(12);
            label.SetHexpand(true);
            _root.Append(label);
            _children.Add((label, null));
            return;
        }

        Gtk.ToggleButton? first = null;
        for (var index = 0; index < _items.Length; index++)
        {
            var button = Gtk.ToggleButton.NewWithLabel(_items[index].Text);
            button.AddCssClass("flat");
            button.AddCssClass(WinoCssClass.Pivot);
            if (button.GetChild() is Gtk.Label text) text.SetEllipsize(Pango.EllipsizeMode.End);
            if (first is null) first = button;
            else button.SetGroup(first);
            var position = index;
            var toggled = GtkSignals.OnToggled(button, () =>
            {
                if (_applying || !button.GetActive()) return;
                _selectedIndex = position;
                SelectionChanged?.Invoke(position);
            });
            _root.Append(button);
            _children.Add((button, toggled));
        }
    }

    private void ApplySelection()
    {
        _applying = true;
        try
        {
            for (var index = 0; index < _children.Count; index++)
            {
                if (_children[index].Widget is Gtk.ToggleButton button && button.GetActive() != (index == _selectedIndex)) button.SetActive(index == _selectedIndex);
            }
        }
        finally { _applying = false; }
    }

    private static void SetText(Gtk.Widget widget, string text)
    {
        if (widget is Gtk.Label label) label.SetText(text);
        else if (widget is Gtk.ToggleButton button) button.SetLabel(text);
    }

    private void Clear()
    {
        foreach (var (widget, toggled) in _children)
        {
            toggled?.Dispose();
            _root.Remove(widget);
        }
        _children.Clear();
    }
}
