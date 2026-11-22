using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Extras;

/// <summary>
/// The day strip of the Daily briefing (macOS WinoDatePager): newer arrow, the selected day's name, older arrow; index 0
/// is the newest day. <see cref="SelectionChanged"/> fires only for user actions.
/// </summary>
public sealed class WinoDatePager : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Button _newer;
    private readonly Gtk.Button _older;
    private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);
    private readonly List<IDisposable> _subscriptions = [];
    private string[] _items = [];
    private int _selectedIndex = -1;

    public WinoDatePager(string newerTooltip, string olderTooltip)
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass("wino-date-pager");
        _newer = WinoIcons.Button(WinoIconGlyph.ArrowLeft, newerTooltip, 12);
        _older = WinoIcons.Button(WinoIconGlyph.ArrowRight, olderTooltip, 12);
        _newer.SetTooltipText(newerTooltip);
        _older.SetTooltipText(olderTooltip);
        _label.AddCssClass("heading");
        _label.SetHexpand(true);
        _label.SetEllipsize(Pango.EllipsizeMode.End);
        _root.Append(_newer);
        _root.Append(_label);
        _root.Append(_older);
        _subscriptions.Add(GtkSignals.OnClicked(_newer, () => SelectedIndex = _selectedIndex - 1));
        _subscriptions.Add(GtkSignals.OnClicked(_older, () => SelectedIndex = _selectedIndex + 1));
        UpdateArrows();
    }

    public Gtk.Widget Root => _root;

    public event Action? SelectionChanged;

    public string SelectedText => _label.GetText();

    public void SetItems(string[] items)
    {
        _items = items;
        if (_selectedIndex >= items.Length) _selectedIndex = items.Length - 1;
        Refresh();
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = WinoPagerState.Clamp(value, _items.Length);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            Refresh();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Sets the index from the ViewModel without raising <see cref="SelectionChanged"/>.</summary>
    public void Select(int index)
    {
        _selectedIndex = WinoPagerState.Clamp(index, _items.Length);
        Refresh();
    }

    public void Dispose()
    {
        SelectionChanged = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void Refresh()
    {
        _label.SetText(_selectedIndex >= 0 && _selectedIndex < _items.Length ? _items[_selectedIndex] : string.Empty);
        WinoA11y.Label(_label, _label.GetText());
        UpdateArrows();
    }

    private void UpdateArrows()
    {
        _newer.SetSensitive(WinoPagerState.CanGoNewer(_selectedIndex));
        _older.SetSensitive(WinoPagerState.CanGoOlder(_selectedIndex, _items.Length));
    }
}
