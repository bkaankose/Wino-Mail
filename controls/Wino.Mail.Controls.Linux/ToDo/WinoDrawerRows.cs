using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.ToDo;

/// <summary>
/// A task drawer row (Windows detail pane buttons, macOS WinoDrawerActionRow): glyph, text and an optional trailing
/// caption in a flat button. The owner reacts to <see cref="Clicked"/>; a menu row's owner opens its menu on
/// <see cref="Root"/>.
/// </summary>
public sealed class WinoDrawerActionRow : IDisposable
{
    private readonly Gtk.Button _button = Gtk.Button.New();
    private readonly Gtk.Label _glyph;
    private readonly Gtk.Label _text = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _trailing = Gtk.Label.New(string.Empty);
    private readonly IDisposable _clicked;
    private WinoIconGlyph _icon;
    private bool _accent;
    private bool _overdue;

    public WinoDrawerActionRow(WinoIconGlyph glyph, string text, bool destructive = false, bool hasMenu = false)
    {
        WinoLiveObjects.Track(this);
        _icon = glyph;
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        content.SetCanTarget(false);
        _glyph = WinoIcons.Label(glyph, 16);
        content.Append(_glyph);
        _text.SetXalign(0);
        _text.SetHexpand(true);
        _text.SetEllipsize(Pango.EllipsizeMode.End);
        content.Append(_text);
        _trailing.AddCssClass("dim-label");
        _trailing.AddCssClass("caption");
        _trailing.SetVisible(false);
        content.Append(_trailing);
        _button.SetChild(content);
        _button.AddCssClass("flat");
        _button.AddCssClass(WinoCssClass.DrawerRow);
        if (destructive) _button.AddCssClass(WinoCssClass.DrawerRowDestructive);
        if (hasMenu) WinoA11y.HasPopup(_button);
        Text = text;
        _clicked = GtkSignals.OnClicked(_button, () => Clicked?.Invoke());
    }

    public Gtk.Widget Root => _button;

    public Gtk.Button Button => _button;

    public string Text
    {
        get => _text.GetText();
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(_text.GetText(), text, StringComparison.Ordinal)) return;
            _text.SetText(text);
            WinoA11y.Label(_button, text);
        }
    }

    /// <summary>A dim caption after the text ("Soon"); it becomes the accessible description.</summary>
    public string? Trailing
    {
        get => _trailing.GetVisible() ? _trailing.GetText() : null;
        set
        {
            _trailing.SetText(value ?? string.Empty);
            _trailing.SetVisible(!string.IsNullOrEmpty(value));
            WinoA11y.Description(_button, value);
        }
    }

    public WinoIconGlyph Glyph
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            WinoIcons.SetGlyph(_glyph, WinoIcons.Glyph(value));
        }
    }

    public bool Sensitive
    {
        get => _button.GetSensitive();
        set => _button.SetSensitive(value);
    }

    /// <summary>The on state of a toggle row (My Day added, a due date set): text and glyph in the accent colour.</summary>
    public bool Accent
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            if (value) _button.AddCssClass(WinoCssClass.DrawerRowAccent);
            else _button.RemoveCssClass(WinoCssClass.DrawerRowAccent);
        }
    }

    public bool Overdue
    {
        get => _overdue;
        set
        {
            if (_overdue == value) return;
            _overdue = value;
            if (value) _button.AddCssClass(WinoCssClass.DrawerRowOverdue);
            else _button.RemoveCssClass(WinoCssClass.DrawerRowOverdue);
        }
    }

    public bool Visible
    {
        get => _button.GetVisible();
        set => _button.SetVisible(value);
    }

    public event Action? Clicked;

    public void Dispose()
    {
        _clicked.Dispose();
        Clicked = null;
    }
}

/// <summary>
/// One step of a task (Windows step template, macOS WinoStepRowView): round check box, the title in a flat entry that
/// commits on Enter and on focus leave, and a delete button. The owner sets every value; the row only raises events.
/// </summary>
public sealed class WinoStepRow : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly WinoRoundCheckbox _check = new();
    private readonly Gtk.Entry _entry = Gtk.Entry.New();
    private readonly Gtk.Button _delete;
    private readonly List<IDisposable> _subscriptions = [];
    private string _title = string.Empty;
    private bool _completed;
    private bool _editable = true;
    private bool _applying;
    private bool _focused;

    public WinoStepRow(string deleteLabel, string toggleLabel)
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass(WinoCssClass.StepRow);
        _check.Label = toggleLabel;
        _root.Append(_check.Root);
        _entry.SetHasFrame(false);
        _entry.SetHexpand(true);
        _root.Append(_entry);
        _delete = WinoIcons.Button(WinoIconGlyph.Dismiss, deleteLabel, 12);
        _delete.SetValign(Gtk.Align.Center);
        _root.Append(_delete);

        _check.Toggled += () => Toggled?.Invoke();
        _subscriptions.Add(GtkSignals.OnClicked(_delete, () => DeleteRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Entry>(h => _entry.OnActivate += h, h => _entry.OnActivate -= h, (_, _) => Commit()));
        var focus = Gtk.EventControllerFocus.New();
        _entry.AddController(focus);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnEnter += h, h => focus.OnEnter -= h, (_, _) => _focused = true));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnLeave += h, h => focus.OnLeave -= h, (_, _) =>
        {
            _focused = false;
            Commit();
        }));
    }

    public Gtk.Widget Root => _root;

    public Gtk.Entry Entry => _entry;

    public WinoRoundCheckbox Check => _check;

    public Gtk.Button DeleteButton => _delete;

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            if (_focused || string.Equals(_entry.GetText(), _title, StringComparison.Ordinal)) return;
            _applying = true;
            try { _entry.SetText(_title); }
            finally { _applying = false; }
            WinoA11y.Label(_entry, _title);
        }
    }

    public bool Completed
    {
        get => _completed;
        set
        {
            _completed = value;
            _check.Checked = value;
            if (value) _root.AddCssClass(WinoCssClass.StepCompleted);
            else _root.RemoveCssClass(WinoCssClass.StepCompleted);
        }
    }

    public bool Editable
    {
        get => _editable;
        set
        {
            _editable = value;
            _entry.SetEditable(value);
            _check.Enabled = value;
            _delete.SetVisible(value);
        }
    }

    public event Action? Toggled;

    /// <summary>Raised with the trimmed text when the user committed a changed, non-empty title.</summary>
    public event Action<string>? Renamed;

    public event Action? DeleteRequested;

    /// <summary>Commits the typed title (Enter, focus leave, or the owner before it rebinds).</summary>
    public void Commit()
    {
        if (_applying || !_editable) return;
        var text = _entry.GetText().Trim();
        if (text.Length == 0)
        {
            _applying = true;
            try { _entry.SetText(_title); }
            finally { _applying = false; }
            return;
        }
        if (string.Equals(text, _title, StringComparison.Ordinal)) return;
        _title = text;
        WinoA11y.Label(_entry, text);
        Renamed?.Invoke(text);
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _check.Dispose();
        Toggled = null;
        Renamed = null;
        DeleteRequested = null;
    }
}

/// <summary>Section pieces of the task drawer (macOS WinoDrawerSection): a caption heading and a vertical group.</summary>
public static class WinoDrawerSection
{
    public static Gtk.Label Label(string text)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.AddCssClass(WinoCssClass.DrawerSectionLabel);
        label.AddCssClass("heading");
        return label;
    }

    public static Gtk.Box Create(params Gtk.Widget[] children)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        foreach (var child in children) box.Append(child);
        return box;
    }
}
