using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Wino.Mail.ViewModels.Links;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Links;

/// <summary>
/// The mails, events and tasks an open item is linked to (Windows linked items list, macOS <c>LinkedItemButtons</c>): an
/// optional caption and one flat button per item with its glyph and title. It follows the shared collection, which the
/// ViewModels fill off the main thread, and rebuilds once per main-loop turn. Hidden while empty.
/// </summary>
internal sealed class LinkedItemStrip : IDisposable
{
    private const int TitleLength = 28;
    private readonly Action<LinkedItemViewModel> _open;
    private readonly Gtk.Box _root;
    private readonly Gtk.Widget _items;
    private readonly Gtk.Orientation _orientation;
    private readonly List<IDisposable> _buttons = [];
    private readonly NotifyCollectionChangedEventHandler _changed;
    private ObservableCollection<LinkedItemViewModel>? _source;
    private LinkedItemViewModel[] _shown = [];
    private int _pending;
    private bool _disposed;

    public LinkedItemStrip(Action<LinkedItemViewModel> open, Gtk.Orientation orientation, string? heading = null)
    {
        WinoLiveObjects.Track(this);
        _open = open;
        _orientation = orientation;
        _changed = (_, _) => RequestRebuild();
        _root = Gtk.Box.New(orientation, orientation == Gtk.Orientation.Horizontal ? 6 : 4);
        _root.AddCssClass("wino-linked-items");
        if (!string.IsNullOrEmpty(heading))
        {
            var caption = Gtk.Label.New(heading);
            caption.AddCssClass("caption");
            caption.AddCssClass("dim-label");
            caption.SetXalign(0);
            caption.SetValign(orientation == Gtk.Orientation.Horizontal ? Gtk.Align.Center : Gtk.Align.Start);
            _root.Append(caption);
        }
        if (orientation == Gtk.Orientation.Horizontal)
        {
            var wrap = Adw.WrapBox.New();
            wrap.SetChildSpacing(4);
            wrap.SetLineSpacing(2);
            wrap.SetHexpand(true);
            _items = wrap;
        }
        else
        {
            _items = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        }
        _root.Append(_items);
        _root.SetVisible(false);
    }

    public Gtk.Widget Root => _root;

    public void Attach(ObservableCollection<LinkedItemViewModel> items)
    {
        if (_disposed || ReferenceEquals(_source, items)) return;
        Detach();
        _source = items;
        items.CollectionChanged += _changed;
        Rebuild();
    }

    public void Detach()
    {
        if (_source is not { } source) return;
        source.CollectionChanged -= _changed;
        _source = null;
        Rebuild();
    }

    public string Describe()
        => string.Join("\n", _shown.Select(static item => $"{item.Glyph}|{item.Title}|{item.Subtitle}|{item.IsAvailable}|{item.IsCompleted}"));

    internal LinkedItemViewModel? ItemAt(int index) => index >= 0 && index < _shown.Length ? _shown[index] : null;

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        ClearButtons();
    }

    private void RequestRebuild()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        MainLoop.Post(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            if (!_disposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        ClearButtons();
        _shown = _source?.ToArray() ?? [];
        foreach (var item in _shown)
        {
            var button = CreateButton(item);
            if (_items is Adw.WrapBox wrap) wrap.Append(button);
            else if (_items is Gtk.Box box) box.Append(button);
        }
        _root.SetVisible(_shown.Length > 0);
    }

    private void ClearButtons()
    {
        foreach (var subscription in _buttons) subscription.Dispose();
        _buttons.Clear();
        if (_items is Adw.WrapBox wrap) wrap.RemoveAll();
        else if (_items is Gtk.Box box)
        {
            while (box.GetFirstChild() is { } child) box.Remove(child);
        }
    }

    private Gtk.Button CreateButton(LinkedItemViewModel item)
    {
        var title = item.Title.Length > TitleLength ? item.Title[..(TitleLength - 1)] + "…" : item.Title;
        var label = Gtk.Label.New(null);
        if (item.IsCompleted) label.SetMarkup("<s>" + GLib.Functions.MarkupEscapeText(title, -1) + "</s>");
        else label.SetText(title);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        label.SetXalign(0);
        label.AddCssClass("caption");
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        content.Append(new WinoIcon(item.Glyph, 14, WinoIconMode.Mono).Root);
        content.Append(label);

        var button = Gtk.Button.New();
        button.SetChild(content);
        button.AddCssClass("flat");
        button.AddCssClass("wino-linked-item");
        if (item.IsCompleted) button.AddCssClass("dim-label");
        if (_orientation == Gtk.Orientation.Vertical) button.SetHalign(Gtk.Align.Start);
        button.SetSensitive(item.IsAvailable);
        if (item.HasSubtitle) button.SetTooltipText(item.Subtitle);
        WinoA11y.Rename(button, item.AutomationName);
        if (item.HasSubtitle) WinoA11y.Description(button, item.Subtitle);
        _buttons.Add(GtkSignals.OnClicked(button, () => _open(item)));
        return button;
    }
}
