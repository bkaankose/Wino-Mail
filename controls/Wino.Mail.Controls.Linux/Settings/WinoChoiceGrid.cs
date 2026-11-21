using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// A wrapping grid of square choice tiles, one radio group (the Windows GridView of a colour or icon card). Each tile
/// shows caller-built content and is named for accessibility. <see cref="Selected"/> reports user choices only; setting
/// <see cref="SelectedIndex"/> never raises it. The owner calls <see cref="Dispose"/>.
/// </summary>
public sealed class WinoChoiceGrid : IDisposable
{
    private const int TileSize = 40;

    private readonly Adw.WrapBox _box;
    private readonly List<Gtk.ToggleButton> _tiles = [];
    private readonly List<IDisposable> _subscriptions = [];
    private int _selected = -1;
    private bool _syncing;

    public WinoChoiceGrid(string accessibleName)
    {
        _box = Adw.WrapBox.New();
        _box.SetChildSpacing(6);
        _box.SetLineSpacing(6);
        WinoA11y.Label(_box, accessibleName);
    }

    public Gtk.Widget Root => _box;

    public int Count => _tiles.Count;

    public event Action<int>? Selected;

    public void Add(Gtk.Widget content, string accessibleName)
    {
        var tile = Gtk.ToggleButton.New();
        tile.AddCssClass("flat");
        tile.AddCssClass("wino-choice-tile");
        tile.SetSizeRequest(TileSize, TileSize);
        content.SetHalign(Gtk.Align.Center);
        content.SetValign(Gtk.Align.Center);
        tile.SetChild(content);
        tile.SetTooltipText(accessibleName);
        WinoA11y.Label(tile, accessibleName);
        if (_tiles.Count > 0) tile.SetGroup(_tiles[0]);
        var index = _tiles.Count;
        _subscriptions.Add(GtkSignals.OnToggled(tile, () =>
        {
            if (_syncing || !tile.GetActive()) return;
            _selected = index;
            Selected?.Invoke(index);
        }));
        _tiles.Add(tile);
        _box.Append(tile);
        Sync();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value;
            Sync();
        }
    }

    public string Describe()
        => string.Join(", ", _tiles.Select((tile, index) => (index == _selected ? "*" : string.Empty) + (tile.GetTooltipText() ?? string.Empty)));

    public void Activate(int index)
    {
        if (index >= 0 && index < _tiles.Count) _tiles[index].SetActive(true);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Selected = null;
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            for (var index = 0; index < _tiles.Count; index++) _tiles[index].SetActive(index == _selected);
            // A radio group keeps one button active; with no selection every tile is turned off explicitly.
            if (_selected < 0 || _selected >= _tiles.Count)
            {
                foreach (var tile in _tiles) tile.SetActive(false);
            }
        }
        finally { _syncing = false; }
    }
}
