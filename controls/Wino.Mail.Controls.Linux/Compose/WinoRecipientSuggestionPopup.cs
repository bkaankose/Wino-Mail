using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Compose;

public sealed record RecipientSuggestionItem(string Address, string? DisplayName, string? SourceLabel, string Query, bool IsList, bool CanRemove, object Tag);

/// <summary>
/// The suggestion list under a recipient entry (Mac <c>ComposeRecipientSuggestionPopup</c>): picture, name with the typed match
/// in bold, address, source and a remove button. It never takes focus; the head drives it from the entry's keys.
/// </summary>
public sealed class WinoRecipientSuggestionPopup : IDisposable
{
    private const int MaxRows = 8;
    private readonly Gtk.Popover _popover = Gtk.Popover.New();
    private readonly Gtk.ListBox _list = Gtk.ListBox.New();
    private readonly List<Row> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private RecipientSuggestionItem[] _items = [];
    private Gtk.Widget? _anchor;
    private bool _disposed;

    public WinoRecipientSuggestionPopup()
    {
        WinoLiveObjects.Track(this);
        _popover.SetAutohide(false);
        _popover.SetCanFocus(false);
        _popover.SetHasArrow(false);
        _popover.SetPosition(Gtk.PositionType.Bottom);
        _popover.AddCssClass(WinoCssClass.RecipientSuggestions);
        _list.SetSelectionMode(Gtk.SelectionMode.Single);
        _list.SetCanFocus(false);
        _list.AddCssClass("navigation-sidebar");
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetPropagateNaturalWidth(true);
        scroller.SetMaxContentHeight(320);
        scroller.SetChild(_list);
        scroller.SetCanFocus(false);
        _popover.SetChild(scroller);
        WinoA11y.Label(_list, Translator.ComposerTo.Trim().TrimEnd(':'));
    }

    public Gtk.Widget Widget => _popover;
    public bool IsOpen => _anchor is not null && _popover.GetVisible();
    public int SelectedIndex { get; private set; } = -1;
    public RecipientSuggestionItem? Selected => SelectedIndex >= 0 && SelectedIndex < _items.Length ? _items[SelectedIndex] : null;
    public RecipientSuggestionItem[] Items => _items;

    /// <summary>The picture of an address (a cache lookup); null shows initials.</summary>
    public Func<string, Gdk.Texture?>? PictureFor { get; set; }

    public event Action<RecipientSuggestionItem>? Chosen;
    public event Action<RecipientSuggestionItem>? RemoveRequested;

    public void Show(Gtk.Widget anchor, RecipientSuggestionItem[] items)
    {
        if (_disposed) return;
        if (items.Length == 0)
        {
            Close();
            return;
        }
        if (!ReferenceEquals(_anchor, anchor))
        {
            Close();
            if (_anchor is not null) _popover.Unparent();
            _popover.SetParent(anchor);
            _anchor = anchor;
        }
        _items = items.Length > MaxRows ? items[..MaxRows] : items;
        while (_rows.Count < _items.Length)
        {
            var row = new Row(this, _rows.Count);
            _rows.Add(row);
            _list.Append(row.Root);
        }
        for (var index = 0; index < _rows.Count; index++)
        {
            var visible = index < _items.Length;
            _rows[index].Root.SetVisible(visible);
            if (visible) _rows[index].Apply(_items[index], PictureFor);
        }
        Select(-1);
        _popover.Popup();
    }

    public void Close()
    {
        if (_anchor is not null && _popover.GetVisible()) _popover.Popdown();
        SelectedIndex = -1;
    }

    /// <summary>Moves the highlight; wraps at both ends. False when closed.</summary>
    public bool MoveSelection(int delta)
    {
        if (!IsOpen || _items.Length == 0) return false;
        var next = SelectedIndex < 0 ? (delta > 0 ? 0 : _items.Length - 1) : ((SelectedIndex + delta) % _items.Length + _items.Length) % _items.Length;
        Select(next);
        return true;
    }

    public void Choose(int index)
    {
        if (index >= 0 && index < _items.Length) Chosen?.Invoke(_items[index]);
    }

    public void Remove(int index)
    {
        if (index >= 0 && index < _items.Length) RemoveRequested?.Invoke(_items[index]);
    }

    private void Select(int index)
    {
        SelectedIndex = index;
        if (index >= 0 && index < _rows.Count) _list.SelectRow(_rows[index].Root);
        else _list.UnselectAll();
    }

    public string Describe() => $"open={IsOpen} selected={SelectedIndex} items={string.Join(" | ", _items.Select(static item => $"{item.DisplayName}<{item.Address}>{(item.IsList ? " list" : "")}{(item.SourceLabel is { } s ? " [" + s + "]" : "")}"))}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Chosen = null;
        RemoveRequested = null;
        PictureFor = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        if (_anchor is not null)
        {
            _popover.Popdown();
            _popover.Unparent();
            _anchor = null;
        }
    }

    internal static string Highlight(string text, string query)
    {
        var at = string.IsNullOrWhiteSpace(query) ? -1 : text.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        if (at < 0) return Escape(text);
        var length = query.Trim().Length;
        return new StringBuilder()
            .Append(Escape(text[..at])).Append("<b>").Append(Escape(text.Substring(at, length))).Append("</b>").Append(Escape(text[(at + length)..]))
            .ToString();
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    private sealed class Row
    {
        private readonly WinoContactPicture _picture = new(WinoContactPicture.RowSize);
        private readonly Gtk.Label _name = Gtk.Label.New(null);
        private readonly Gtk.Label _address = Gtk.Label.New(null);
        private readonly Gtk.Label _source = Gtk.Label.New(null);
        private readonly Gtk.Button _choose = Gtk.Button.New();
        private readonly Gtk.Button _remove;

        public Row(WinoRecipientSuggestionPopup owner, int index)
        {
            Root = Gtk.ListBoxRow.New();
            Root.SetCanFocus(false);
            Root.SetActivatable(false);
            _name.SetXalign(0);
            _name.SetEllipsize(Pango.EllipsizeMode.End);
            _name.SetUseMarkup(true);
            _address.SetXalign(0);
            _address.SetEllipsize(Pango.EllipsizeMode.End);
            _address.AddCssClass("dim-label");
            _address.AddCssClass("caption");
            _address.SetUseMarkup(true);
            _source.AddCssClass("wino-recipient-source");
            _source.SetValign(Gtk.Align.Center);
            var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            texts.SetHexpand(true);
            texts.SetValign(Gtk.Align.Center);
            texts.Append(_name);
            texts.Append(_address);
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            content.Append(_picture.Root);
            content.Append(texts);
            content.Append(_source);
            _choose.SetChild(content);
            _choose.AddCssClass("flat");
            _choose.SetCanFocus(false);
            _choose.SetFocusOnClick(false);
            _choose.SetHexpand(true);
            _remove = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.Buttons_Remove, 12);
            _remove.SetCanFocus(false);
            _remove.SetFocusOnClick(false);
            _remove.SetValign(Gtk.Align.Center);
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            box.SetSizeRequest(300, -1);
            box.Append(_choose);
            box.Append(_remove);
            Root.SetChild(box);
            owner._subscriptions.Add(GtkSignals.OnClicked(_choose, () => owner.Choose(index)));
            owner._subscriptions.Add(GtkSignals.OnClicked(_remove, () => owner.Remove(index)));
        }

        public Gtk.ListBoxRow Root { get; }

        public void Apply(RecipientSuggestionItem item, Func<string, Gdk.Texture?>? pictures)
        {
            var name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Address : item.DisplayName;
            _name.SetMarkup(Highlight(name, item.Query));
            _address.SetMarkup(Highlight(item.IsList ? item.SourceLabel ?? string.Empty : item.Address, item.Query));
            _source.SetText(item.IsList ? string.Empty : item.SourceLabel ?? string.Empty);
            _source.SetVisible(!item.IsList && !string.IsNullOrWhiteSpace(item.SourceLabel));
            _picture.SetIdentity(name, item.Address);
            _picture.Picture = pictures?.Invoke(item.Address);
            _remove.SetVisible(item.CanRemove);
            var accessible = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Address : $"{item.DisplayName} {item.Address}";
            WinoA11y.Label(Root, accessible);
            WinoA11y.Rename(_choose, accessible);
            WinoA11y.Rename(_remove, $"{Translator.Buttons_Remove} {item.Address}");
        }
    }
}
