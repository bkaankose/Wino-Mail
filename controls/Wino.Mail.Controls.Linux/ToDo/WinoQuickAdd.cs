using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.ToDo;

/// <summary>
/// The quick add card under the task list (Windows composer border, macOS WinoQuickAddView): an accent Add glyph, a
/// frameless entry and the Add button, which shows while the composer is expanded. Enter submits.
/// </summary>
public sealed class WinoQuickAdd : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly Gtk.Entry _entry = Gtk.Entry.New();
    private readonly Gtk.Button _add;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _applying;
    private bool _focused;

    public WinoQuickAdd(string addTitle)
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass(WinoCssClass.QuickAdd);
        _root.AddCssClass("card");
        var glyph = WinoIcons.Label(WinoIconGlyph.Add, 14);
        glyph.AddCssClass("accent");
        glyph.SetCanTarget(false);
        _root.Append(glyph);
        _entry.SetHasFrame(false);
        _entry.SetHexpand(true);
        _root.Append(_entry);
        _add = Gtk.Button.NewWithLabel(addTitle);
        _add.AddCssClass("suggested-action");
        _add.SetValign(Gtk.Align.Center);
        _add.SetVisible(false);
        WinoA11y.Rename(_add, addTitle);
        _root.Append(_add);

        _subscriptions.Add(GtkSignals.Connect<Gtk.Entry>(h => _entry.OnActivate += h, h => _entry.OnActivate -= h, (_, _) => Submitted?.Invoke()));
        _subscriptions.Add(GtkSignals.OnClicked(_add, () => Submitted?.Invoke()));
        _subscriptions.Add(GtkSignals.OnChanged(_entry, () =>
        {
            if (!_applying) TextChanged?.Invoke();
        }));
        var focus = Gtk.EventControllerFocus.New();
        _entry.AddController(focus);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnEnter += h, h => focus.OnEnter -= h, (_, _) => SetFocused(true)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => focus.OnLeave += h, h => focus.OnLeave -= h, (_, _) => SetFocused(false)));
    }

    public Gtk.Widget Root => _root;

    public Gtk.Entry Entry => _entry;

    public Gtk.Button AddButton => _add;

    public string Text
    {
        get => _entry.GetText();
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(_entry.GetText(), text, StringComparison.Ordinal)) return;
            _applying = true;
            try { _entry.SetText(text); }
            finally { _applying = false; }
        }
    }

    /// <summary>The placeholder, which is also the entry's accessible name.</summary>
    public string Placeholder
    {
        set
        {
            _entry.SetPlaceholderText(value ?? string.Empty);
            WinoA11y.Label(_entry, value);
        }
    }

    public bool Enabled
    {
        set => _entry.SetSensitive(value);
    }

    public bool ShowButton
    {
        set => _add.SetVisible(value);
    }

    public bool IsFocused => _focused;

    public event Action? Submitted;

    public event Action? TextChanged;

    public event Action? FocusChanged;

    public void Focus() => _entry.GrabFocus();

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Submitted = null;
        TextChanged = null;
        FocusChanged = null;
    }

    private void SetFocused(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;
        FocusChanged?.Invoke();
    }
}
