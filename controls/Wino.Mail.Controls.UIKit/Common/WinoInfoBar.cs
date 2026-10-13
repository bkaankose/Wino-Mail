using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Common;

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
    /// <summary>The user tapped the close (X) button.</summary>
    CloseButton,
    /// <summary>The <see cref="WinoInfoBar.AutoDismissInterval"/> elapsed.</summary>
    AutoDismiss
}

public sealed class WinoInfoBarClosedEventArgs(WinoInfoBarCloseReason reason) : EventArgs
{
    public WinoInfoBarCloseReason Reason { get; } = reason;
}

/// <summary>
/// Inline status banner (Windows InfoBar; UIKit twin of the AppKit <c>WinoInfoBar</c>): a tinted rounded box with the
/// severity glyph from the WinoIcons font, a bold title followed by the secondary message as one wrapping paragraph,
/// an optional action button and an optional close button at the trailing edge. Like the Windows bar it can dismiss
/// itself after <see cref="AutoDismissInterval"/> on screen; the countdown restarts when the content changes while
/// visible, pauses while the iPad pointer is over it, and stops when the bar is hidden or leaves its window. Closing by
/// button or timer hides the bar and raises <see cref="Closed"/>; hiding it from code does not.
/// </summary>
public sealed class WinoInfoBar : WinoSurfaceView
{
    private const double VerticalPadding = 10;
    private const double LeadingPadding = 12;
    private const double TrailingPadding = 8;
    private const double IconSize = 16;

    private readonly WinoIconView _glyph;
    private readonly UILabel _text;
    private readonly UIButton _action;
    private readonly UIButton _close;
    private readonly UIStackView _accessories;
    private readonly NSLayoutConstraint _textTrailingToAccessories;
    private readonly NSLayoutConstraint _textTrailingToEdge;
    private WinoInfoBarSeverity _severity;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private TimeSpan? _autoDismissInterval;
    private NSTimer? _dismissTimer;
    private bool _isPointerInside;

    public WinoInfoBar(WinoInfoBarSeverity severity = WinoInfoBarSeverity.Informational, string? title = null, string? message = null)
    {
        CornerRadius = WinoStyle.GroupRadius;
        StrokeWidth = 1;

        _glyph = new WinoIconView(WinoIconGlyph.Info, IconSize) { Colorful = false };
        WinoLayout.Size(_glyph, IconSize, IconSize);

        _text = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.PrimaryText, 0);
        _text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _text.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Vertical);

        var actionConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        actionConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        _action = new UIButton { Configuration = actionConfiguration, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(_action, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        _action.PrimaryActionTriggered += (_, _) => ActionInvoked?.Invoke(this, EventArgs.Empty);

        var closeConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        closeConfiguration.Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12, null, Translator.Buttons_Close);
        closeConfiguration.BaseForegroundColor = UIColor.SecondaryLabel;
        closeConfiguration.ContentInsets = new NSDirectionalEdgeInsets(4, 4, 4, 4);
        _close = new UIButton { Configuration = closeConfiguration, Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(_close, Translator.Buttons_Close);
        _close.PrimaryActionTriggered += (_, _) => Dismiss(WinoInfoBarCloseReason.CloseButton);
        WinoLayout.Size(_close, 32, 32);

        _accessories = WinoLayout.HStack(8, _action, _close);
        WinoLayout.Priorities(_accessories, UILayoutConstraintAxis.Horizontal, 1000, 1000);

        AddSubview(_glyph);
        AddSubview(_text);
        AddSubview(_accessories);

        // The icon is centred on the first text line, so it stays put when the message wraps.
        var firstLineCentre = Math.Ceiling(WinoStyle.Description.LineHeight) / 2;
        _textTrailingToAccessories = _text.TrailingAnchor.ConstraintEqualTo(_accessories.LeadingAnchor, -12);
        _textTrailingToEdge = _text.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-LeadingPadding));
        NSLayoutConstraint.ActivateConstraints(
        [
            _glyph.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)LeadingPadding),
            _glyph.CenterYAnchor.ConstraintEqualTo(_text.TopAnchor, (nfloat)firstLineCentre),

            _text.LeadingAnchor.ConstraintEqualTo(_glyph.TrailingAnchor, 8),
            _text.TopAnchor.ConstraintEqualTo(TopAnchor, (nfloat)VerticalPadding),
            _text.BottomAnchor.ConstraintEqualTo(BottomAnchor, (nfloat)(-VerticalPadding)),

            _accessories.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, (nfloat)(-TrailingPadding)),
            _accessories.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _accessories.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 4),
            _accessories.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, -4)
        ]);

        AddGestureRecognizer(new UIHoverGestureRecognizer(PointerChanged));
        IsAccessibilityElement = false;
        ShouldGroupAccessibilityChildren = true;
        Severity = severity;
        Title = title;
        Message = message;
        UpdateAccessories();
    }

    /// <summary>The severity glyph and hue: Info, CheckmarkCircleFilled, Warning and DismissCircle from the WinoIcons font.</summary>
    public static (WinoIconGlyph Glyph, UIColor Color) SeverityAppearance(WinoInfoBarSeverity severity) => severity switch
    {
        WinoInfoBarSeverity.Success => (WinoIconGlyph.CheckmarkCircleFilled, UIColor.SystemGreen),
        WinoInfoBarSeverity.Warning => (WinoIconGlyph.Warning, UIColor.SystemOrange),
        WinoInfoBarSeverity.Error => (WinoIconGlyph.DismissCircle, UIColor.SystemRed),
        _ => (WinoIconGlyph.Info, UIColor.SystemBlue)
    };

    public WinoInfoBarSeverity Severity
    {
        get => _severity;
        set
        {
            bool changed = _severity != value;
            _severity = value;
            var (glyph, color) = SeverityAppearance(value);
            _glyph.Icon = glyph;
            _glyph.Tint = color;
            // System colours stay dynamic through the alpha change, so the tint follows the appearance.
            Fill = WinoStyle.Dynamic(color.ColorWithAlpha(0.10f), color.ColorWithAlpha(0.18f));
            Stroke = WinoStyle.Dynamic(color.ColorWithAlpha(0.28f), color.ColorWithAlpha(0.35f));
            if (changed) RestartAutoDismissIfShowing();
        }
    }

    public string? Title
    {
        get => _title;
        set
        {
            var text = value ?? string.Empty;
            if (text == _title && _text.AttributedText is not null) return;
            _title = text;
            UpdateText();
            RestartAutoDismissIfShowing();
        }
    }

    public string? Message
    {
        get => _message;
        set
        {
            var text = value ?? string.Empty;
            if (text == _message && _text.AttributedText is not null) return;
            _message = text;
            UpdateText();
            RestartAutoDismissIfShowing();
        }
    }

    /// <summary>Shows the action button with this title; null hides it.</summary>
    public string? ActionTitle
    {
        get => _action.Hidden ? null : _action.Configuration?.Title;
        set
        {
            var title = string.IsNullOrEmpty(value) ? null : value;
            if (ActionTitle == title) return;
            if (_action.Configuration is { } configuration)
            {
                configuration.Title = title ?? string.Empty;
                _action.Configuration = configuration;
            }
            _action.Hidden = title is null;
            UpdateAccessories();
            RestartAutoDismissIfShowing();
        }
    }

    /// <summary>Shows the trailing close (X) button (WinUI InfoBar.IsClosable). Off by default.</summary>
    public bool IsClosable
    {
        get => !_close.Hidden;
        set { _close.Hidden = !value; UpdateAccessories(); }
    }

    /// <summary>
    /// How long the bar stays on screen before it hides itself and raises <see cref="Closed"/> with
    /// <see cref="WinoInfoBarCloseReason.AutoDismiss"/>. Null, zero or negative keeps the bar open.
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

    /// <summary>Holds the auto-dismiss countdown while the iPad pointer is over the bar; it restarts in full on exit.</summary>
    public bool PausesAutoDismissOnHover { get; set; } = true;

    public event EventHandler? ActionInvoked;

    /// <summary>The bar closed itself, by the close button or the auto-dismiss timer. It is already hidden.</summary>
    public event EventHandler<WinoInfoBarClosedEventArgs>? Closed;

    /// <summary>Starts the auto-dismiss countdown over, for example when the same message is shown again.</summary>
    public void RestartAutoDismiss() => RestartAutoDismissIfShowing();

    public override bool Hidden
    {
        get => base.Hidden;
        set
        {
            base.Hidden = value;
            if (value) StopAutoDismiss();
            else
            {
                _isPointerInside = false;
                RestartAutoDismissIfShowing();
            }
        }
    }

    private bool IsShowing
    {
        get
        {
            if (Window is null) return false;
            for (UIView? view = this; view is not null; view = view.Superview)
                if (view.Hidden) return false;
            return true;
        }
    }

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
        base.Hidden = true;
        Closed?.Invoke(this, new WinoInfoBarClosedEventArgs(reason));
    }

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        _isPointerInside = false;
        RestartAutoDismissIfShowing();
    }

    private void PointerChanged(UIHoverGestureRecognizer recognizer)
    {
        bool inside = recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;
        if (inside == _isPointerInside) return;
        _isPointerInside = inside;
        if (!PausesAutoDismissOnHover) return;
        if (inside) StopAutoDismiss();
        else if (_dismissTimer is null) RestartAutoDismissIfShowing();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAutoDismiss();
            ActionInvoked = null;
            Closed = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Title and message flow as one paragraph: bold title, then the message in secondary text.</summary>
    private void UpdateText()
    {
        var text = new NSMutableAttributedString();
        if (_title.Length > 0)
            text.Append(new NSAttributedString(_title, new UIStringAttributes { Font = WinoStyle.Scaled(13, UIFontWeight.Semibold), ForegroundColor = UIColor.Label }));
        if (_message.Length > 0)
        {
            text.Append(new NSAttributedString((_title.Length > 0 ? "  " : string.Empty) + _message, new UIStringAttributes
            {
                Font = WinoStyle.Description,
                ForegroundColor = _title.Length > 0 ? UIColor.SecondaryLabel : UIColor.Label
            }));
        }
        _text.AttributedText = text;
        _text.AccessibilityLabel = string.Join(". ", new[] { _title, _message }.Where(part => part.Length > 0));
    }

    private void UpdateAccessories()
    {
        bool any = !_action.Hidden || !_close.Hidden;
        _accessories.Hidden = !any;
        _textTrailingToEdge.Active = !any;
        _textTrailingToAccessories.Active = any;
    }
}
