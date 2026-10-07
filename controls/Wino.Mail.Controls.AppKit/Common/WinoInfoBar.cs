using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Common;

public enum WinoInfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

/// <summary>Why a <see cref="WinoInfoBar"/> closed itself.</summary>
public enum WinoInfoBarCloseReason
{
    /// <summary>The user clicked the close (X) button.</summary>
    CloseButton,
    /// <summary>The <see cref="WinoInfoBar.AutoDismissInterval"/> elapsed.</summary>
    AutoDismiss
}

public sealed class WinoInfoBarClosedEventArgs(WinoInfoBarCloseReason reason) : EventArgs
{
    public WinoInfoBarCloseReason Reason { get; } = reason;
}

/// <summary>
/// Inline status banner (Windows InfoBar), drawn the way macOS shows inline banners: a tinted
/// rounded box with a severity SF Symbol, a bold title followed by a secondary message that wraps
/// as one paragraph, an optional action button and an optional borderless close button pinned to
/// the trailing edge. The icon sits on the first text line; the action and close buttons are
/// vertically centred. Never use an alert for a status.
/// <para>
/// Like the Windows WinoInfoBar, the bar can dismiss itself: set <see cref="AutoDismissInterval"/> and it
/// hides after that long on screen. The countdown starts whenever the bar becomes visible in a window,
/// restarts when its content changes while visible, pauses while the pointer is over it, and stops when
/// the bar (or an ancestor) is hidden or leaves its window. Closing by button or timer hides the bar and
/// raises <see cref="Closed"/>, so pages can mirror WinUI's two-way IsOpen binding into their ViewModel.
/// Hiding the bar from code never raises <see cref="Closed"/>.
/// </para>
/// </summary>
public sealed class WinoInfoBar : WinoSurfaceView
{
    private const double VerticalPadding = 10;
    private const double LeadingPadding = 12;
    private const double TrailingPadding = 8;
    private const double IconSize = 16;

    private readonly NSImageView _glyph;
    private readonly NSTextField _text;
    private readonly NSButton _action;
    private readonly NSButton _close;
    private readonly NSStackView _accessories;
    private readonly NSLayoutConstraint _textTrailingToAccessories;
    private readonly NSLayoutConstraint _textTrailingToEdge;
    private WinoInfoBarSeverity _severity;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private TimeSpan? _autoDismissInterval;
    private NSTimer? _dismissTimer;
    private NSTrackingArea? _trackingArea;
    private bool _isPointerInside;

    public WinoInfoBar(WinoInfoBarSeverity severity = WinoInfoBarSeverity.Informational, string? title = null, string? message = null)
    {
        CornerRadius = WinoStyle.GroupRadius;
        StrokeWidth = 1;

        _glyph = new NSImageView
        {
            ImageScaling = NSImageScale.ProportionallyDown,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _glyph.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _glyph.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);

        _text = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _text.Selectable = false;
        _text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _text.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Vertical);

        _action = new NSButton
        {
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Small,
            Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize),
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _action.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _action.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _action.Activated += (_, _) => ActionInvoked?.Invoke(this, EventArgs.Empty);

        _close = new NSButton
        {
            Bordered = false,
            Image = WinoStyle.Symbol("xmark", Translator.Buttons_Close, 11, NSFontWeight.Semibold),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ImageScaling = NSImageScale.ProportionallyDown,
            ContentTintColor = NSColor.SecondaryLabel,
            ToolTip = Translator.Buttons_Close,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _close.SetButtonType(NSButtonType.MomentaryChange);
        WinoAccessibility.Label(_close, Translator.Buttons_Close);
        _close.Activated += (_, _) => Dismiss(WinoInfoBarCloseReason.CloseButton);

        _accessories = WinoLayout.HStack(8, _action, _close);
        _accessories.Alignment = NSLayoutAttribute.CenterY;
        _accessories.SetHuggingPriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _accessories.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _accessories.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);

        AddSubview(_glyph);
        AddSubview(_text);
        AddSubview(_accessories);

        // The icon is centred on the first text line, so it stays put when the message wraps.
        var firstLineCentre = Math.Ceiling(WinoStyle.Body.Ascender - WinoStyle.Body.Descender + WinoStyle.Body.Leading) / 2;
        _textTrailingToAccessories = _text.TrailingAnchor.ConstraintEqualTo(_accessories.LeadingAnchor, -12);
        _textTrailingToEdge = _text.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-LeadingPadding));
        NSLayoutConstraint.ActivateConstraints(
        [
            _glyph.WidthAnchor.ConstraintEqualTo((nfloat)IconSize),
            _glyph.HeightAnchor.ConstraintEqualTo((nfloat)IconSize),
            _glyph.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)LeadingPadding),
            _glyph.CenterYAnchor.ConstraintEqualTo(_text.TopAnchor, (nfloat)firstLineCentre),

            _text.LeadingAnchor.ConstraintEqualTo(_glyph.TrailingAnchor, 8),
            _text.TopAnchor.ConstraintEqualTo(TopAnchor, (nfloat)VerticalPadding),
            _text.BottomAnchor.ConstraintEqualTo(BottomAnchor, (nfloat)(-VerticalPadding)),

            _accessories.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-TrailingPadding)),
            _accessories.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _accessories.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 4),
            _accessories.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, -4),

            _close.WidthAnchor.ConstraintEqualTo(20),
            _close.HeightAnchor.ConstraintEqualTo(20)
        ]);

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.GroupRole;
        Severity = severity;
        Title = title;
        Message = message;
        UpdateAccessories();
    }

    public WinoInfoBarSeverity Severity
    {
        get => _severity;
        set
        {
            bool changed = _severity != value;
            _severity = value;
            var (symbol, color) = value switch
            {
                WinoInfoBarSeverity.Success => ("checkmark.circle.fill", NSColor.SystemGreen),
                WinoInfoBarSeverity.Warning => ("exclamationmark.triangle.fill", NSColor.SystemOrange),
                WinoInfoBarSeverity.Error => ("xmark.octagon.fill", NSColor.SystemRed),
                _ => ("info.circle.fill", NSColor.SystemBlue)
            };
            _glyph.Image = WinoStyle.Symbol(symbol, value.ToString(), 15);
            _glyph.ContentTintColor = color;
            // System colours stay dynamic through the alpha change, so the tint follows the appearance.
            Fill = WinoStyle.Dynamic(color.ColorWithAlphaComponent(0.10f), color.ColorWithAlphaComponent(0.18f));
            Stroke = WinoStyle.Dynamic(color.ColorWithAlphaComponent(0.28f), color.ColorWithAlphaComponent(0.35f));
            if (changed) RestartAutoDismissIfShowing();
        }
    }

    public string? Title
    {
        get => _title;
        set
        {
            var text = value ?? string.Empty;
            if (text == _title && AccessibilityLabel == value) return;
            _title = text; AccessibilityLabel = value; UpdateText(); RestartAutoDismissIfShowing();
        }
    }

    public string? Message
    {
        get => _message;
        set
        {
            var text = value ?? string.Empty;
            if (text == _message && AccessibilityHelp == value) return;
            _message = text; AccessibilityHelp = value; UpdateText(); RestartAutoDismissIfShowing();
        }
    }

    /// <summary>Shows the action button with this title; null hides it.</summary>
    public string? ActionTitle
    {
        get => _action.Hidden ? null : _action.Title;
        set
        {
            if (ActionTitle == (string.IsNullOrEmpty(value) ? null : value)) return;
            _action.Title = value ?? string.Empty; _action.Hidden = string.IsNullOrEmpty(value); UpdateAccessories(); RestartAutoDismissIfShowing();
        }
    }

    /// <summary>
    /// Shows the trailing close (X) button (WinUI InfoBar.IsClosable). Off by default. When off, the
    /// action button stays pinned to the trailing edge and, with no action either, the text takes the full width.
    /// </summary>
    public bool IsClosable
    {
        get => !_close.Hidden;
        set { _close.Hidden = !value; UpdateAccessories(); }
    }

    /// <summary>
    /// How long the bar stays on screen before it hides itself and raises <see cref="Closed"/> with
    /// <see cref="WinoInfoBarCloseReason.AutoDismiss"/> (Windows WinoInfoBar.DismissInterval). Null, zero
    /// or negative keeps the bar open until it is closed or hidden.
    /// </summary>
    public TimeSpan? AutoDismissInterval
    {
        get => _autoDismissInterval;
        set
        {
            _autoDismissInterval = value is { } interval && interval > TimeSpan.Zero ? interval : null;
            RestartAutoDismissIfShowing();
        }
    }

    /// <summary>Holds the auto-dismiss countdown while the pointer is over the bar; it restarts in full on exit.</summary>
    public bool PausesAutoDismissOnHover { get; set; } = true;

    public event EventHandler? ActionInvoked;

    /// <summary>The bar closed itself, by the close button or the auto-dismiss timer. It is already hidden.</summary>
    public event EventHandler<WinoInfoBarClosedEventArgs>? Closed;

    /// <summary>Starts the auto-dismiss countdown over, for example when the same message is shown again.</summary>
    public void RestartAutoDismiss() => RestartAutoDismissIfShowing();

    private bool IsShowing => Window is not null && !IsHiddenOrHasHiddenAncestor;

    private void RestartAutoDismissIfShowing()
    {
        StopAutoDismiss();
        if (_autoDismissInterval is not { } interval || !IsShowing) return;
        if (PausesAutoDismissOnHover && _isPointerInside) return;
        // The run loop retains the timer, so the block only holds the bar weakly.
        var self = new WeakReference<WinoInfoBar>(this);
        _dismissTimer = NSTimer.CreateScheduledTimer(interval.TotalSeconds, _ =>
        {
            if (self.TryGetTarget(out var bar)) bar.OnAutoDismissElapsed();
        });
    }

    private void StopAutoDismiss()
    {
        _dismissTimer?.Invalidate();
        _dismissTimer?.Dispose();
        _dismissTimer = null;
    }

    private void OnAutoDismissElapsed()
    {
        StopAutoDismiss();
        if (IsShowing) Dismiss(WinoInfoBarCloseReason.AutoDismiss);
    }

    private void Dismiss(WinoInfoBarCloseReason reason)
    {
        StopAutoDismiss();
        Hidden = true;
        Closed?.Invoke(this, new WinoInfoBarClosedEventArgs(reason));
    }

    public override void ViewDidUnhide()
    {
        base.ViewDidUnhide();
        // The pointer may have left while the bar was hidden; the tracking area reports a fresh entry.
        _isPointerInside = false;
        RestartAutoDismissIfShowing();
    }

    public override void ViewDidHide()
    {
        base.ViewDidHide();
        StopAutoDismiss();
    }

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        _isPointerInside = false;
        RestartAutoDismissIfShowing();
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_trackingArea is not null) RemoveTrackingArea(_trackingArea);
        _trackingArea = new NSTrackingArea(Bounds,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInActiveApp | NSTrackingAreaOptions.InVisibleRect,
            this, null);
        AddTrackingArea(_trackingArea);
    }

    public override void MouseEntered(NSEvent theEvent)
    {
        base.MouseEntered(theEvent);
        _isPointerInside = true;
        if (PausesAutoDismissOnHover) StopAutoDismiss();
    }

    public override void MouseExited(NSEvent theEvent)
    {
        base.MouseExited(theEvent);
        _isPointerInside = false;
        if (PausesAutoDismissOnHover && _dismissTimer is null) RestartAutoDismissIfShowing();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopAutoDismiss();
        base.Dispose(disposing);
    }

    public override void Layout()
    {
        base.Layout();
        // Wrapping labels need their width to report the right height to Auto Layout.
        var width = _text.Frame.Width;
        if (width > 0 && Math.Abs(_text.PreferredMaxLayoutWidth - width) > 0.5)
        {
            _text.PreferredMaxLayoutWidth = width;
            base.Layout();
        }
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    /// <summary>Title and message flow as one paragraph: bold title, then the message in secondary text.</summary>
    private void UpdateText()
    {
        var text = new NSMutableAttributedString();
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = NSLineBreakMode.ByWordWrapping };
        if (_title.Length > 0)
        {
            text.Append(new NSAttributedString(_title, new NSStringAttributes
            {
                Font = WinoStyle.BodyStrong,
                ForegroundColor = NSColor.Label,
                ParagraphStyle = paragraph
            }));
        }
        if (_message.Length > 0)
        {
            text.Append(new NSAttributedString((_title.Length > 0 ? "  " : string.Empty) + _message, new NSStringAttributes
            {
                Font = WinoStyle.Body,
                ForegroundColor = _title.Length > 0 ? NSColor.SecondaryLabel : NSColor.Label,
                ParagraphStyle = paragraph
            }));
        }
        _text.AttributedStringValue = text;
        NeedsLayout = true;
    }

    private void UpdateAccessories()
    {
        bool any = !_action.Hidden || !_close.Hidden;
        _accessories.Hidden = !any;
        _textTrailingToEdge.Active = !any;
        _textTrailingToAccessories.Active = any;
    }
}
