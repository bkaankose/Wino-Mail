using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>A filter token of the scope bar. Active tokens are tinted; a removable token shows a dismiss glyph and is named by <see cref="RemoveLabel"/>.</summary>
public readonly record struct WinoSearchChip(int Tag, string Text, bool IsActive = false, bool IsRemovable = false, string? RemoveLabel = null);

/// <summary>
/// The bar above a searched mail list (AppKit <c>WinoSearchScopeBar</c>): Local / Online, the scope and Done on line 1; the
/// result status, value tokens, the Unread / Has attachments / Flagged toggles, the Filters token and Clear all on line 2.
/// Both lines wrap and the status sits on line 2, so the bar never widens the narrowest mail list pane.
/// Setters raise no events; the page is the source of truth and re-applies state after each search.
/// </summary>
public sealed class WinoSearchScopeBar : IDisposable
{
    private const string LocalName = "local";
    private const string OnlineName = "online";

    private readonly Gtk.Box _root;
    private readonly Adw.ToggleGroup _reach;
    private readonly Gtk.DropDown _scope;
    private readonly Gtk.Label _status;
    private readonly Adw.WrapBox _tokens;
    private readonly Gtk.Button _filters;
    private readonly Gtk.Button _clearAll;
    private readonly List<ChipSlot> _chips = [];
    private readonly List<IDisposable> _subscriptions = [];
    private string[] _scopeTitles = [];
    private bool _updating;
    private bool _disposed;

    public WinoSearchScopeBar(string localTitle, string onlineTitle, string scopeLabel, string doneTitle)
    {
        WinoLiveObjects.Track(this);
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        _root.AddCssClass(WinoCssClass.SearchScopeBar);

        _reach = Adw.ToggleGroup.New();
        _reach.Add(Toggle(LocalName, localTitle));
        _reach.Add(Toggle(OnlineName, onlineTitle));
        _reach.SetActiveName(LocalName);
        _reach.SetValign(Gtk.Align.Center);
        _reach.SetHexpand(false);
        _reach.SetCanShrink(false);

        _scope = Gtk.DropDown.New(Gtk.StringList.New([]), null);
        _scope.SetValign(Gtk.Align.Center);
        _scope.SetHexpand(false);
        WinoA11y.Label(_scope, scopeLabel);

        _status = Gtk.Label.New(string.Empty);
        _status.AddCssClass("caption");
        _status.AddCssClass("dim-label");
        _status.SetValign(Gtk.Align.Center);
        _status.SetVisible(false);

        var done = Gtk.Button.NewWithLabel(doneTitle);
        done.AddCssClass("flat");
        done.SetValign(Gtk.Align.Center);

        // Wraps too: the three controls side by side are wider than the narrowest mail list pane.
        var line1 = Adw.WrapBox.New();
        line1.SetChildSpacing(8);
        line1.SetLineSpacing(6);
        line1.Append(_reach);
        line1.Append(_scope);
        line1.Append(done);

        _filters = Gtk.Button.NewWithLabel(string.Empty);
        _filters.AddCssClass(WinoCssClass.SearchChip);
        _filters.SetVisible(false);
        _clearAll = Gtk.Button.NewWithLabel(string.Empty);
        _clearAll.AddCssClass("flat");
        _clearAll.AddCssClass("caption");
        _clearAll.SetVisible(false);

        _tokens = Adw.WrapBox.New();
        _tokens.SetChildSpacing(6);
        _tokens.SetLineSpacing(6);
        _tokens.Append(_status);
        _tokens.Append(_filters);
        _tokens.Append(_clearAll);

        _root.Append(line1);
        _root.Append(_tokens);

        var filters = _filters;
        _subscriptions.Add(GtkSignals.OnNotify(_reach, "active", () =>
        {
            if (!_updating) ReachChanged?.Invoke();
        }));
        _subscriptions.Add(GtkSignals.OnNotify(_scope, "selected", () =>
        {
            if (!_updating) ScopeChanged?.Invoke((int)_scope.GetSelected());
        }));
        _subscriptions.Add(GtkSignals.OnClicked(done, () => DoneClicked?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_filters, () => FiltersClicked?.Invoke(filters)));
        _subscriptions.Add(GtkSignals.OnClicked(_clearAll, () => ClearAllClicked?.Invoke()));
    }

    /// <summary>Hidden by the owner while no search is active.</summary>
    public Gtk.Widget Root => _root;

    public event Action? ReachChanged;

    /// <summary>An index into the titles of <see cref="SetScopes"/>.</summary>
    public event Action<int>? ScopeChanged;

    public event Action? DoneClicked;

    public event Action<WinoSearchChip>? ChipClicked;

    /// <summary>The Filters token, as a popover anchor.</summary>
    public event Action<Gtk.Widget>? FiltersClicked;

    public event Action? ClearAllClicked;

    public bool IsOnline
    {
        get => string.Equals(_reach.GetActiveName(), OnlineName, StringComparison.Ordinal);
        set
        {
            if (IsOnline == value) return;
            _updating = true;
            try { _reach.SetActiveName(value ? OnlineName : LocalName); }
            finally { _updating = false; }
        }
    }

    public int SelectedScope => (int)_scope.GetSelected();

    public string StatusText => _status.GetText();

    public Gtk.Widget FiltersButton => _filters;

    /// <summary>The shown tokens, in order.</summary>
    public WinoSearchChip[] Chips => [.. _chips.Where(static slot => slot.Button.GetVisible()).Select(static slot => slot.Chip)];

    public bool IsClearAllVisible => _clearAll.GetVisible();

    public bool IsFiltersVisible => _filters.GetVisible();

    public void SetScopes(string[] titles, int selectedIndex)
    {
        _updating = true;
        try
        {
            if (!titles.SequenceEqual(_scopeTitles, StringComparer.Ordinal))
            {
                _scopeTitles = titles;
                _scope.SetModel(Gtk.StringList.New(titles));
            }
            if (selectedIndex >= 0 && selectedIndex < titles.Length && _scope.GetSelected() != (uint)selectedIndex) _scope.SetSelected((uint)selectedIndex);
        }
        finally { _updating = false; }
    }

    /// <summary>An empty text hides the label.</summary>
    public void SetStatus(string text)
    {
        if (!string.Equals(_status.GetText(), text, StringComparison.Ordinal)) _status.SetText(text);
        _status.SetVisible(text.Length > 0);
    }

    public void SetChips(WinoSearchChip[] chips)
    {
        if (_chips.Count < chips.Length)
        {
            _tokens.Remove(_filters);
            _tokens.Remove(_clearAll);
            while (_chips.Count < chips.Length)
            {
                var slot = new ChipSlot(this);
                _chips.Add(slot);
                _tokens.Append(slot.Button);
            }
            _tokens.Append(_filters);
            _tokens.Append(_clearAll);
        }
        for (var index = 0; index < _chips.Count; index++)
        {
            if (index < chips.Length) _chips[index].Apply(chips[index]);
            else _chips[index].Button.SetVisible(false);
        }
    }

    /// <summary>A null <paramref name="title"/> hides the Filters token; an <paramref name="activeCount"/> above zero tints it and shows the number.</summary>
    public void SetFilters(string? title, int activeCount)
    {
        _filters.SetVisible(title is not null);
        if (title is null) return;
        _filters.SetLabel(activeCount > 0 ? $"{title} ({activeCount})" : title);
        Toggle(_filters, WinoCssClass.Active, activeCount > 0);
    }

    /// <summary>A null <paramref name="title"/> or no active filter hides the link.</summary>
    public void SetClearAll(string? title, int activeCount)
    {
        var visible = title is not null && activeCount > 0;
        _clearAll.SetVisible(visible);
        if (visible && title is not null) _clearAll.SetLabel(title);
    }

    /// <summary>Raises the events a click on the token at <paramref name="index"/> raises (keyboard and agent checks).</summary>
    public void ActivateChip(int index)
    {
        if (index >= 0 && index < _chips.Count && _chips[index].Button.GetVisible()) ChipClicked?.Invoke(_chips[index].Chip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var slot in _chips) slot.Dispose();
        _chips.Clear();
        ReachChanged = null;
        ScopeChanged = null;
        DoneClicked = null;
        ChipClicked = null;
        FiltersClicked = null;
        ClearAllClicked = null;
    }

    private static Adw.Toggle Toggle(string name, string label)
    {
        var toggle = Adw.Toggle.New();
        toggle.SetName(name);
        toggle.SetLabel(label);
        return toggle;
    }

    private static void Toggle(Gtk.Widget widget, string cssClass, bool on)
    {
        if (on) widget.AddCssClass(cssClass);
        else widget.RemoveCssClass(cssClass);
    }

    private sealed class ChipSlot : IDisposable
    {
        private readonly Gtk.Label _text;
        private readonly Gtk.Label _dismiss;
        private readonly IDisposable _clicked;
        private string _name = string.Empty;

        public ChipSlot(WinoSearchScopeBar owner)
        {
            Button = Gtk.Button.New();
            Button.AddCssClass(WinoCssClass.SearchChip);
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            _text = Gtk.Label.New(string.Empty);
            _text.SetEllipsize(Pango.EllipsizeMode.End);
            _text.SetMaxWidthChars(36);
            _dismiss = WinoIcons.Label(WinoIconGlyph.Dismiss, 9);
            _dismiss.SetValign(Gtk.Align.Center);
            _dismiss.SetVisible(false);
            content.Append(_text);
            content.Append(_dismiss);
            Button.SetChild(content);
            _clicked = GtkSignals.OnClicked(Button, () => owner.ChipClicked?.Invoke(Chip));
        }

        public Gtk.Button Button { get; }

        public WinoSearchChip Chip { get; private set; }

        public void Apply(WinoSearchChip chip)
        {
            Button.SetVisible(true);
            if (chip == Chip) return;
            Chip = chip;
            if (!string.Equals(_text.GetText(), chip.Text, StringComparison.Ordinal)) _text.SetText(chip.Text);
            _dismiss.SetVisible(chip.IsRemovable);
            Toggle(Button, WinoCssClass.Active, chip.IsActive);
            var name = chip.IsRemovable ? chip.RemoveLabel ?? chip.Text : chip.Text;
            if (string.Equals(name, _name, StringComparison.Ordinal)) return;
            _name = name;
            Button.SetTooltipText(name);
            WinoA11y.Label(Button, name);
        }

        public void Dispose() => _clicked.Dispose();
    }
}
