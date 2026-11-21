using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Shell;

/// <summary>
/// The mode switcher at the foot of the sidebar: Mail, Calendar, Contacts and To Do, then Settings behind a divider.
/// A click only raises <see cref="ModeSelected"/>; the host decides whether the mode becomes <see cref="Selected"/>.
/// Settings opens its own window, so it is never shown as selected.
/// </summary>
public sealed class WinoModeSwitcher : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 2);
    private readonly List<Segment> _segments = [];
    private readonly List<IDisposable> _signals = [];
    private WinoApplicationMode _selected = WinoApplicationMode.Mail;
    private bool _applying;
    private bool _disposed;

    public WinoModeSwitcher()
    {
        _root.AddCssClass(WinoCssClass.ModeSwitcher);
        _root.SetMarginStart(8);
        _root.SetMarginEnd(8);
        _root.SetMarginTop(6);
        _root.SetMarginBottom(10);
        WinoA11y.Label(_root, Translator.KeyboardShortcuts_Mode);

        foreach (var segment in WinoModeSegments.All)
        {
            var mode = segment.Mode;
            var icon = new WinoIcon(segment.Regular, 18, WinoIconMode.Mono);
            var label = $"{segment.Title} ({ShortcutLabel(mode)})";
            if (mode == WinoApplicationMode.Settings)
            {
                var separator = Gtk.Separator.New(Gtk.Orientation.Vertical);
                separator.SetMarginTop(4);
                separator.SetMarginBottom(4);
                _root.Append(separator);
                var settings = Gtk.Button.New();
                settings.AddCssClass("flat");
                settings.SetSizeRequest(40, 32);
                settings.SetChild(icon.Root);
                WinoA11y.LabelWithTooltip(settings, label);
                _signals.Add(GtkSignals.OnClicked(settings, () => ModeSelected?.Invoke(WinoApplicationMode.Settings)));
                _root.Append(settings);
                continue;
            }

            var button = Gtk.ToggleButton.New();
            button.AddCssClass("flat");
            button.SetHexpand(true);
            button.SetSizeRequest(-1, 32);
            button.SetChild(icon.Root);
            WinoA11y.LabelWithTooltip(button, label);
            _segments.Add(new Segment(segment, button, icon));
            _signals.Add(GtkSignals.OnToggled(button, () => OnToggled(mode)));
            _root.Append(button);
        }
        ApplySelection();
    }

    public Gtk.Widget Root => _root;

    /// <summary>The app mode shown as active. Settings is ignored.</summary>
    public WinoApplicationMode Selected
    {
        get => _selected;
        set
        {
            if (value == WinoApplicationMode.Settings || value == _selected) return;
            _selected = value;
            ApplySelection();
        }
    }

    public event Action<WinoApplicationMode>? ModeSelected;

    /// <summary>Shows only these modes plus Settings; null shows every segment.</summary>
    public void SetVisibleModes(IReadOnlyCollection<WinoApplicationMode>? visible)
    {
        foreach (var segment in _segments) segment.Button.SetVisible(visible is null || visible.Contains(segment.Info.Mode));
    }

    public static string ShortcutLabel(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail => "Ctrl+1",
        WinoApplicationMode.Calendar => "Ctrl+2",
        WinoApplicationMode.Contacts => "Ctrl+3",
        WinoApplicationMode.Tasks => "Ctrl+4",
        _ => "Ctrl+,"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var signal in _signals) signal.Dispose();
        _signals.Clear();
        ModeSelected = null;
    }

    private void OnToggled(WinoApplicationMode mode)
    {
        if (_applying) return;
        ApplySelection();
        if (mode != _selected) ModeSelected?.Invoke(mode);
    }

    private void ApplySelection()
    {
        _applying = true;
        try
        {
            foreach (var segment in _segments)
            {
                var active = segment.Info.Mode == _selected;
                segment.Button.SetActive(active);
                segment.Icon.SetIcon(active ? segment.Info.Filled : segment.Info.Regular);
            }
        }
        finally { _applying = false; }
    }

    private sealed record Segment(WinoModeSegment Info, Gtk.ToggleButton Button, WinoIcon Icon);
}
