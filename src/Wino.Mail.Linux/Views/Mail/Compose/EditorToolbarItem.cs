using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// One command of an <see cref="EditorFormatToolbar"/> row: its widget and the entry it becomes in the row's More menu
/// when it does not fit. <see cref="IsAvailable"/> false hides it in both places.
/// </summary>
internal sealed class EditorToolbarItem
{
    public EditorToolbarItem(Gtk.Widget widget, Action<WinoMenuBuilder>? menuItem, Func<bool>? isAvailable = null)
    {
        Widget = widget;
        MenuItem = menuItem;
        IsAvailable = isAvailable;
    }

    public Gtk.Widget Widget { get; }
    public Action<WinoMenuBuilder>? MenuItem { get; }
    public Func<bool>? IsAvailable { get; }
    public bool IsDivider { get; private init; }
    public string Name => IsDivider ? "|" : Widget.GetTooltipText() ?? Widget.GetType().Name;

    public static EditorToolbarItem Divider()
    {
        var separator = Gtk.Separator.New(Gtk.Orientation.Vertical);
        separator.SetMarginTop(6);
        separator.SetMarginBottom(6);
        separator.SetMarginStart(4);
        separator.SetMarginEnd(4);
        return new EditorToolbarItem(separator, null) { IsDivider = true };
    }
}

/// <summary>A flat icon button, or a toggle that shows its state; the label is the tooltip and the accessible name.</summary>
internal sealed class EditorFormatButton : IDisposable
{
    private readonly Gtk.Button _button;
    private readonly Gtk.Label _glyph;
    private readonly IDisposable _clicked;
    private bool _applying;

    public EditorFormatButton(WinoIconGlyph glyph, string label, Action action, bool toggle = false, string? tooltip = null)
    {
        Label = label;
        Glyph = glyph;
        _button = toggle ? WinoIcons.ToggleButton(glyph, label) : WinoIcons.Button(glyph, label);
        _glyph = (Gtk.Label)(_button.GetChild() ?? throw new InvalidOperationException("The icon button has no glyph."));
        if (tooltip is not null) _button.SetTooltipText(tooltip);
        _button.AddCssClass("wino-editor-tool");
        _clicked = _button is Gtk.ToggleButton toggleButton
            ? GtkSignals.OnToggled(toggleButton, () => { if (!_applying) action(); })
            : GtkSignals.OnClicked(_button, action);
    }

    public string Label { get; private set; }
    public WinoIconGlyph Glyph { get; private set; }
    public Gtk.Button Root => _button;

    public bool IsActive
    {
        get => _button is Gtk.ToggleButton toggle && toggle.GetActive();
        set
        {
            if (_button is not Gtk.ToggleButton toggle || toggle.GetActive() == value) return;
            _applying = true;
            try { toggle.SetActive(value); }
            finally { _applying = false; }
        }
    }

    public bool Enabled
    {
        get => _button.GetSensitive();
        set => _button.SetSensitive(value);
    }

    public void SetGlyph(WinoIconGlyph glyph, string? label = null)
    {
        WinoIcons.SetGlyph(_glyph, WinoIcons.Glyph(glyph));
        Glyph = glyph;
        if (label is null) return;
        Label = label;
        _button.SetTooltipText(label);
        WinoA11y.Label(_button, label);
    }

    public void Dispose() => _clicked.Dispose();
}

/// <summary>
/// A row of toolbar items that never widens its pane: the items that do not fit the width the owner reports
/// (<see cref="SetAvailableWidth"/>) move, from the end, into the More menu (Mac <c>EditorOverflowRow</c>).
/// </summary>
internal sealed class EditorOverflowRow : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.ScrolledWindow _clip = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _row = Gtk.Box.New(Gtk.Orientation.Horizontal, 1);
    private readonly Gtk.Button _more = WinoIcons.Button(WinoIconGlyph.More, Translator.Composer_AttachmentMoreOptions);
    private readonly IDisposable _moreClicked;
    private readonly List<EditorToolbarItem> _items = [];
    private readonly List<EditorToolbarItem> _overflow = [];
    private int _available = -1;
    private WeakReference<WinoMenuPopover>? _menu;

    public EditorOverflowRow()
    {
        _root.AccessibleRole = Gtk.AccessibleRole.Toolbar;
        WinoA11y.Label(_root, Translator.Composer_EditorToolbarLabel);
        // External: the row's minimum width stays near zero, so a stale layout clips for a frame instead of widening the pane.
        _clip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        _clip.SetPropagateNaturalWidth(true);
        _clip.SetPropagateNaturalHeight(true);
        _clip.SetHasFrame(false);
        _clip.SetHexpand(true);
        _clip.SetChild(_row);
        _row.SetHalign(Gtk.Align.Start);
        _more.AddCssClass("wino-editor-tool");
        _more.SetVisible(false);
        _moreClicked = GtkSignals.OnClicked(_more, ShowOverflowMenu);
        _root.Append(_clip);
        _root.Append(_more);
    }

    public Gtk.Widget Root => _root;
    public Gtk.Widget MoreButton => _more;
    public IReadOnlyList<EditorToolbarItem> Items => _items;
    public IReadOnlyList<EditorToolbarItem> OverflowItems => _overflow;
    public bool IsOverflowing => _more.GetVisible();
    public bool IsEmpty => _items.All(static item => item.IsDivider);

    public void SetItems(IEnumerable<EditorToolbarItem> items)
    {
        foreach (var item in _items) _row.Remove(item.Widget);
        _items.Clear();
        _items.AddRange(items);
        foreach (var item in _items) _row.Append(item.Widget);
        Layout();
    }

    public void SetAvailableWidth(int width)
    {
        if (width == _available) return;
        _available = width;
        Layout();
    }

    /// <summary>Re-measures after an item changed its width or availability.</summary>
    public void Layout()
    {
        _overflow.Clear();
        var candidates = _items.Where(static item => item.IsAvailable?.Invoke() ?? true).ToList();
        foreach (var item in _items) item.Widget.SetVisible(candidates.Contains(item));
        TrimDividers(candidates);
        if (_available < 0)
        {
            _more.SetVisible(false);
            return;
        }

        _more.SetVisible(true);
        _more.Measure(Gtk.Orientation.Horizontal, -1, out _, out var moreWidth, out _, out _);
        var total = Width(candidates);
        if (total <= _available)
        {
            _more.SetVisible(false);
            return;
        }

        var budget = _available - moreWidth;
        var used = 0;
        var fits = 0;
        for (; fits < candidates.Count; fits++)
        {
            var width = Width(candidates[fits]);
            if (used + width > budget) break;
            used += width + 1;
        }
        foreach (var item in candidates.Skip(fits))
        {
            item.Widget.SetVisible(false);
            if (!item.IsDivider) _overflow.Add(item);
        }
        while (fits > 0 && candidates[fits - 1].IsDivider) candidates[--fits].Widget.SetVisible(false);
        _more.SetVisible(_overflow.Count > 0);
    }

    public void ShowOverflowMenu() => OpenOverflowMenu();

    public WinoMenuPopover? OpenOverflowMenu()
    {
        if (_overflow.Count == 0) return null;
        var builder = WinoMenus.Create();
        foreach (var item in _overflow) item.MenuItem?.Invoke(builder);
        if (builder.IsEmpty) return null;
        var menu = ContextMenu.Show(_more, builder.Build());
        _menu = new WeakReference<WinoMenuPopover>(menu);
        return menu;
    }

    private static void TrimDividers(List<EditorToolbarItem> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (!item.IsDivider) continue;
            var previousIsDivider = index == 0 || items[index - 1].IsDivider;
            if (index == items.Count - 1 || previousIsDivider)
            {
                item.Widget.SetVisible(false);
                items.RemoveAt(index);
            }
        }
    }

    private static int Width(List<EditorToolbarItem> items) => items.Sum(Width) + Math.Max(0, items.Count - 1);

    private static int Width(EditorToolbarItem item)
    {
        item.Widget.Measure(Gtk.Orientation.Horizontal, -1, out _, out var natural, out _, out _);
        return natural;
    }

    public void Dispose()
    {
        if (_menu is { } menu && menu.TryGetTarget(out var popover)) popover.Popdown();
        _moreClicked.Dispose();
        foreach (var item in _items) _row.Remove(item.Widget);
        _items.Clear();
        _overflow.Clear();
    }
}
