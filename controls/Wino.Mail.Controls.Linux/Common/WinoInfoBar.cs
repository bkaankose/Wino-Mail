using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Common;

public enum WinoInfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

public enum WinoInfoBarCloseReason
{
    CloseButton,
    AutoDismiss,
}

/// <summary>
/// The Wino info bar: severity glyph, a bold title over the message, an optional action and close button, and an
/// optional auto dismiss that pauses while the pointer rests on it. Text is never markup. <see cref="Closed"/> is
/// raised only for the close button or the timer.
/// </summary>
public sealed class WinoInfoBar : IDisposable
{
    private readonly Gtk.Box _root;
    private readonly Gtk.Label _glyph;
    private readonly Gtk.Label _title;
    private readonly Gtk.Label _message;
    private readonly Adw.Bin _content;
    private readonly Gtk.Box _accessories;
    private readonly Gtk.Button _action;
    private readonly Gtk.Button _close;
    private readonly List<IDisposable> _subscriptions = [];
    private WinoInfoBarSeverity? _severity;
    private string? _titleText;
    private string? _messageText;
    private TimeSpan? _autoDismissInterval;
    private MainLoop.Throttle? _timer;
    private DateTime _dueAt;
    private bool _hovered;
    private bool _disposed;

    public WinoInfoBar(WinoInfoBarSeverity severity = WinoInfoBarSeverity.Informational, string? title = null, string? message = null)
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _root.AccessibleRole = Gtk.AccessibleRole.Alert;
        _root.AddCssClass(WinoCssClass.InfoBar);

        _glyph = WinoIcons.Label(string.Empty, 16);
        _glyph.AddCssClass(WinoCssClass.InfoBarGlyph);
        _glyph.SetValign(Gtk.Align.Start);
        _glyph.SetMarginTop(2);
        _root.Append(_glyph);

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetHexpand(true);
        _title = TextLabel();
        _title.AddCssClass("heading");
        _message = TextLabel();
        _content = Adw.Bin.New();
        _content.SetVisible(false);
        text.Append(_title);
        text.Append(_message);
        text.Append(_content);
        _root.Append(text);

        _accessories = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _accessories.SetValign(Gtk.Align.Center);
        _action = Gtk.Button.NewWithLabel(string.Empty);
        _action.SetVisible(false);
        _close = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.Buttons_Close, 12);
        _close.SetVisible(false);
        _accessories.Append(_action);
        _accessories.Append(_close);
        _root.Append(_accessories);

        _subscriptions.Add(GtkSignals.OnClicked(_action, () => ActionInvoked?.Invoke(this)));
        _subscriptions.Add(GtkSignals.OnClicked(_close, () => Close(WinoInfoBarCloseReason.CloseButton)));
        var motion = Gtk.EventControllerMotion.New();
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.EnterSignalArgs>(
            h => motion.OnEnter += h, h => motion.OnEnter -= h, (_, _) => SetHovered(true)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion>(
            h => motion.OnLeave += h, h => motion.OnLeave -= h, (_, _) => SetHovered(false)));
        _root.AddController(motion);

        Severity = severity;
        Title = title;
        Message = message;
    }

    public Gtk.Widget Root => _root;

    public event Action<WinoInfoBar>? ActionInvoked;

    /// <summary>Raised after the bar hid itself through the close button or the timer.</summary>
    public event Action<WinoInfoBar, WinoInfoBarCloseReason>? Closed;

    public WinoInfoBarSeverity Severity
    {
        get => _severity ?? WinoInfoBarSeverity.Informational;
        set
        {
            if (_severity == value) return;
            if (_severity is { } previous) _root.RemoveCssClass(SeverityClass(previous));
            _severity = value;
            _root.AddCssClass(SeverityClass(value));
            WinoIcons.SetGlyph(_glyph, WinoIcons.Glyph(value switch
            {
                WinoInfoBarSeverity.Success => WinoIconGlyph.CheckmarkCircle,
                WinoInfoBarSeverity.Warning => WinoIconGlyph.Warning,
                WinoInfoBarSeverity.Error => WinoIconGlyph.DismissCircle,
                _ => WinoIconGlyph.Info,
            }));
        }
    }

    public string? Title
    {
        get => _titleText;
        set
        {
            _titleText = value;
            SetText(_title, value);
        }
    }

    public string? Message
    {
        get => _messageText;
        set
        {
            _messageText = value;
            SetText(_message, value);
        }
    }

    /// <summary>Null or empty hides the action button.</summary>
    public string? ActionTitle
    {
        get => _action.GetVisible() ? _action.GetLabel() : null;
        set
        {
            _action.SetLabel(value ?? string.Empty);
            _action.SetVisible(!string.IsNullOrEmpty(value));
        }
    }

    public bool IsClosable
    {
        get => _close.GetVisible();
        set => _close.SetVisible(value);
    }

    /// <summary>An optional widget under the text.</summary>
    public Gtk.Widget? Content
    {
        get => _content.GetChild();
        set
        {
            _content.SetChild(value);
            _content.SetVisible(value is not null);
            _accessories.SetValign(value is null ? Gtk.Align.Center : Gtk.Align.Start);
        }
    }

    /// <summary>The bar's visibility. Setting it never raises <see cref="Closed"/>; hiding cancels the timer.</summary>
    public bool IsOpen
    {
        get => _root.GetVisible();
        set
        {
            _root.SetVisible(value);
            if (value)
            {
                // A host can be built before the translations load; the close label is read again when the bar opens.
                WinoA11y.LabelWithTooltip(_close, Translator.Buttons_Close);
                RestartAutoDismiss();
            }
            else CancelTimer();
        }
    }

    /// <summary>Null keeps the bar until it is closed.</summary>
    public TimeSpan? AutoDismissInterval
    {
        get => _autoDismissInterval;
        set
        {
            if (_autoDismissInterval == value) return;
            _autoDismissInterval = value;
            _timer?.Dispose();
            _timer = value is { } interval && interval > TimeSpan.Zero
                ? new MainLoop.Throttle((uint)Math.Min(uint.MaxValue, interval.TotalMilliseconds), () => Close(WinoInfoBarCloseReason.AutoDismiss))
                : null;
            if (IsOpen) RestartAutoDismiss();
        }
    }

    public bool PausesAutoDismissOnHover { get; set; } = true;

    public void RestartAutoDismiss()
    {
        if (_disposed || _timer is null || _autoDismissInterval is not { } interval || !_root.GetVisible()) return;
        if (_hovered && PausesAutoDismissOnHover)
        {
            CancelTimer();
            return;
        }
        _dueAt = DateTime.UtcNow + interval;
        _timer.Request();
    }

    /// <summary>The pointer entered or left the bar; leaving restarts the full interval.</summary>
    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        if (!PausesAutoDismissOnHover) return;
        if (hovered) CancelTimer();
        else RestartAutoDismiss();
    }

    /// <summary><c>severity|title|message|action|closable|open|remaining_ms|hovered</c>, for Debug checks.</summary>
    public string Describe()
    {
        var remaining = _dueAt == default ? -1 : Math.Max(0, (int)(_dueAt - DateTime.UtcNow).TotalMilliseconds);
        return string.Join('|', Severity, Title ?? string.Empty, Message ?? string.Empty, ActionTitle ?? string.Empty,
            IsClosable, IsOpen, remaining.ToString(CultureInfo.InvariantCulture), _hovered);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        ActionInvoked = null;
        Closed = null;
    }

    private void Close(WinoInfoBarCloseReason reason)
    {
        if (_disposed) return;
        IsOpen = false;
        Closed?.Invoke(this, reason);
    }

    private void CancelTimer()
    {
        _dueAt = default;
        _timer?.Cancel();
    }

    private void SetText(Gtk.Label label, string? text)
    {
        label.SetText(text ?? string.Empty);
        label.SetVisible(!string.IsNullOrEmpty(text));
        WinoA11y.Label(_root, AccessibleText(_titleText, _messageText));
    }

    /// <summary>"Title. Message", without a second full stop when the title already ends a sentence.</summary>
    internal static string AccessibleText(string? title, string? message)
    {
        var head = title?.Trim() ?? string.Empty;
        var tail = message?.Trim() ?? string.Empty;
        if (head.Length == 0) return tail;
        if (tail.Length == 0) return head;
        return head[^1] is '.' or '!' or '?' or '…' or '。' ? $"{head} {tail}" : $"{head}. {tail}";
    }

    private static string SeverityClass(WinoInfoBarSeverity severity) => severity switch
    {
        WinoInfoBarSeverity.Success => "wino-info-bar-success",
        WinoInfoBarSeverity.Warning => "wino-info-bar-warning",
        WinoInfoBarSeverity.Error => "wino-info-bar-error",
        _ => "wino-info-bar-informational",
    };

    private static Gtk.Label TextLabel()
    {
        var label = Gtk.Label.New(string.Empty);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.SetWidthChars(20);
        label.SetMaxWidthChars(60);
        label.SetVisible(false);
        return label;
    }
}
