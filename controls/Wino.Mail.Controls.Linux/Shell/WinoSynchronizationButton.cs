using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Shell;

/// <summary>
/// The header's synchronize button: the sync glyph while idle and a spinner while synchronizing, in a stack that keeps
/// the button's size. Providers raise bursts of notifications, so every setter skips values that did not change.
/// </summary>
public sealed class WinoSynchronizationButton : IDisposable
{
    private const string IdlePage = "idle";
    private const string BusyPage = "busy";

    private string _defaultLabel;
    private readonly Gtk.Button _button = Gtk.Button.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.Label _idle;
    private readonly IDisposable _clicked;
    private bool? _supported;
    private bool? _enabled;
    private bool _synchronizing;
    private string? _toolTip;
    private string? _attention;
    private string? _appliedLabel;

    public WinoSynchronizationButton(string defaultLabel)
    {
        _defaultLabel = defaultLabel;
        _idle = WinoIcons.Label(WinoIconGlyph.Sync, 16);
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(16, 16);
        _stack.SetTransitionType(Gtk.StackTransitionType.None);
        _stack.SetHhomogeneous(true);
        _stack.SetVhomogeneous(true);
        _stack.AddNamed(_idle, IdlePage);
        _stack.AddNamed(spinner, BusyPage);
        _stack.SetVisibleChildName(IdlePage);
        _button.SetChild(_stack);
        _button.AddCssClass("flat");
        _button.SetValign(Gtk.Align.Center);
        _clicked = GtkSignals.OnClicked(_button, () => Clicked?.Invoke());
        ApplyLabel();
    }

    public Gtk.Widget Root => _button;
    public bool IsSynchronizing => _synchronizing;
    public string? Attention => _attention;
    public string ToolTip => _appliedLabel ?? _defaultLabel;
    public event Action? Clicked;

    /// <summary><paramref name="supported"/> false hides the button; <paramref name="enabled"/> false keeps it visible and insensitive.</summary>
    public void SetState(bool supported, bool enabled, bool isSynchronizing, string? toolTip)
    {
        if (_supported != supported)
        {
            _supported = supported;
            _button.SetVisible(supported);
        }

        var sensitive = enabled && !isSynchronizing;
        if (_enabled != sensitive)
        {
            _enabled = sensitive;
            _button.SetSensitive(sensitive);
        }

        if (_synchronizing != isSynchronizing)
        {
            _synchronizing = isSynchronizing;
            _stack.SetVisibleChildName(isSynchronizing ? BusyPage : IdlePage);
        }

        _toolTip = toolTip;
        ApplyLabel();
    }

    /// <summary>The label shown without a state tooltip; a language change sets it again.</summary>
    public void SetDefaultLabel(string label)
    {
        _defaultLabel = label;
        ApplyLabel();
    }

    /// <summary>A warning state on the idle glyph (relay fallback, failed sync) with its own tooltip; null clears it.</summary>
    public void SetAttention(string? toolTip)
    {
        var text = string.IsNullOrWhiteSpace(toolTip) ? null : toolTip;
        if (_attention == text) return;
        _attention = text;
        if (text is null) _idle.RemoveCssClass("warning");
        else _idle.AddCssClass("warning");
        ApplyLabel();
    }

    public void Dispose()
    {
        Clicked = null;
        _clicked.Dispose();
    }

    private void ApplyLabel()
    {
        var label = _attention is not null && !_synchronizing
            ? _attention
            : string.IsNullOrWhiteSpace(_toolTip) ? _defaultLabel : _toolTip;
        if (_appliedLabel == label) return;
        _appliedLabel = label;
        WinoA11y.LabelWithTooltip(_button, label);
    }
}
