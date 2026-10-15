using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

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
/// Inline status banner (Windows InfoBar; UIKit and AppKit <c>WinoInfoBar</c>, same members): a tinted rounded box with
/// the severity glyph, the title in medium weight followed by the message as one wrapping paragraph, an optional action
/// and an optional close button. It can dismiss itself after <see cref="AutoDismissInterval"/> on screen; the countdown
/// restarts when the content changes while shown. Closing by button or timer hides the bar and raises
/// <see cref="Closed"/>; hiding it from code does not. Usable from XML as <c>com.winomail.controls.WinoInfoBar</c>.
/// </summary>
[Register("com.winomail.controls.WinoInfoBar")]
public class WinoInfoBar : LinearLayout
{
    private readonly GradientDrawable _fill = new();
    private readonly Handler _handler = new(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
    private WinoIconView? _glyph;
    private TextView? _text;
    private MaterialButton? _action;
    private ImageButton? _close;
    private LinearLayout? _content;
    private WinoInfoBarSeverity _severity;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private TimeSpan? _autoDismissInterval;
    private int _dismissVersion;

    public WinoInfoBar(Context context) : base(context) => Initialize(context);
    public WinoInfoBar(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoInfoBar(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize(Context context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        int horizontal = WinoUnits.Dp(context, 12);
        int vertical = WinoUnits.Dp(context, 8);
        SetPadding(horizontal, vertical, WinoUnits.Dp(context, 6), vertical);
        SetMinimumHeight(WinoUnits.Dp(context, 48));
        _fill.SetCornerRadius(WinoUnits.DpF(context, 8));
        Background = _fill;
        LayoutInflater.From(context)?.Inflate(Resource.Layout.wino_info_bar, this, true);
        _glyph = FindViewById<WinoIconView>(Resource.Id.wino_info_bar_glyph);
        _text = FindViewById<TextView>(Resource.Id.wino_info_bar_text);
        _action = FindViewById<MaterialButton>(Resource.Id.wino_info_bar_action);
        _close = FindViewById<ImageButton>(Resource.Id.wino_info_bar_close);
        _content = FindViewById<LinearLayout>(Resource.Id.wino_info_bar_content);
        if (_glyph is not null) _glyph.GlyphSize = 18;
        if (_action is not null)
        {
            // The canvas action: a white (zone) button with a hairline stroke, not the Material tonal colour.
            _action.BackgroundTintList = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.CardFill(context));
            _action.StrokeColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.CardStroke(context));
            _action.StrokeWidth = WinoUnits.Dp(context, 1);
            _action.SetTextColor(WinoColors.PrimaryText(context));
        }
        if (_action is not null) _action.Click += (_, _) => ActionInvoked?.Invoke(this, EventArgs.Empty);
        if (_close is not null)
        {
            _close.SetImageDrawable(WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, 12, WinoColors.SecondaryText(context)));
            _close.ContentDescription = Translator.Buttons_Close;
            _close.Click += (_, _) => Dismiss(WinoInfoBarCloseReason.CloseButton);
        }
        Severity = WinoInfoBarSeverity.Informational;
    }

    /// <summary>
    /// The action sits beside the paragraph; when it would take more than half of the room the paragraph gets (large font
    /// scales, narrow windows), it moves under the paragraph, start-aligned.
    /// </summary>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        if (_content is null || _action is null || _text is null)
        {
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
            return;
        }
        SetStacked(false);
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        if (_action.Visibility == ViewStates.Visible && _action.MeasuredWidth > _content.MeasuredWidth / 2)
        {
            SetStacked(true);
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        }
    }

    private void SetStacked(bool stacked)
    {
        if (_content is null || _action is null || _text is null || (_content.Orientation == Orientation.Vertical) == stacked) return;
        _content.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
        if (_text.LayoutParameters is LinearLayout.LayoutParams text)
        {
            text.Width = stacked ? ViewGroup.LayoutParams.MatchParent : 0;
            text.Weight = stacked ? 0 : 1;
        }
        if (_action.LayoutParameters is LinearLayout.LayoutParams action && Context is { } context)
        {
            action.MarginStart = stacked ? 0 : WinoTokens.Px(context, Resource.Dimension.wino_info_bar_action_spacing);
            action.TopMargin = stacked ? WinoTokens.Px(context, Resource.Dimension.wino_info_bar_action_stacked_spacing) : 0;
            action.Gravity = stacked ? GravityFlags.Start : GravityFlags.NoGravity;
        }
    }

    /// <summary>The severity glyph and hue: Info, CheckmarkCircleFilled, Warning and DismissCircle from the WinoIcons font.</summary>
    public static (WinoIconGlyph Glyph, Color Color) SeverityAppearance(Context context, WinoInfoBarSeverity severity) => severity switch
    {
        WinoInfoBarSeverity.Success => (WinoIconGlyph.CheckmarkCircleFilled, WinoColors.Success(context)),
        WinoInfoBarSeverity.Warning => (WinoIconGlyph.Warning, WinoColors.Caution(context)),
        WinoInfoBarSeverity.Error => (WinoIconGlyph.DismissCircle, WinoColors.Critical(context)),
        _ => (WinoIconGlyph.Info, WinoColors.Accent(context))
    };

    public WinoInfoBarSeverity Severity
    {
        get => _severity;
        set
        {
            _severity = value;
            if (Context is not { } context) return;
            var (glyph, color) = SeverityAppearance(context, value);
            if (_glyph is not null)
            {
                _glyph.Icon = glyph;
                _glyph.SetTextColor(color);
            }
            _fill.SetColor(WinoColors.FromResource(context, value switch
            {
                WinoInfoBarSeverity.Success => Resource.Color.wino_success_background,
                WinoInfoBarSeverity.Warning => Resource.Color.wino_warning_background,
                WinoInfoBarSeverity.Error => Resource.Color.wino_error_background,
                _ => Resource.Color.wino_info_background
            }));
        }
    }

    public string? Title
    {
        get => _title;
        set { _title = value ?? string.Empty; UpdateText(); }
    }

    public string? Message
    {
        get => _message;
        set { _message = value ?? string.Empty; UpdateText(); }
    }

    /// <summary>The action button's title; null or empty hides the button.</summary>
    public string? ActionTitle
    {
        get => _action?.Text;
        set
        {
            if (_action is null) return;
            _action.Text = value;
            _action.Visibility = string.IsNullOrEmpty(value) ? ViewStates.Gone : ViewStates.Visible;
        }
    }

    public bool IsClosable
    {
        get => _close?.Visibility == ViewStates.Visible;
        set { if (_close is not null) _close.Visibility = value ? ViewStates.Visible : ViewStates.Gone; }
    }

    /// <summary>Time on screen before the bar closes itself; null keeps it open.</summary>
    public TimeSpan? AutoDismissInterval
    {
        get => _autoDismissInterval;
        set { _autoDismissInterval = value; RestartAutoDismiss(); }
    }

    public event EventHandler? ActionInvoked;

    /// <summary>Raised after the close button or the timer hid the bar.</summary>
    public event EventHandler<WinoInfoBarClosedEventArgs>? Closed;

    /// <summary>Shows or hides the bar from code (no <see cref="Closed"/>).</summary>
    public bool IsOpen
    {
        get => Visibility == ViewStates.Visible;
        set
        {
            Visibility = value ? ViewStates.Visible : ViewStates.Gone;
            RestartAutoDismiss();
        }
    }

    /// <summary>Starts the countdown again (a new message while the bar is up).</summary>
    public void RestartAutoDismiss()
    {
        int version = ++_dismissVersion;
        if (_autoDismissInterval is not { } interval || !IsOpen || !IsAttachedToWindow) return;
        _handler.PostDelayed(() =>
        {
            if (version == _dismissVersion && IsOpen) Dismiss(WinoInfoBarCloseReason.AutoDismiss);
        }, (long)interval.TotalMilliseconds);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        RestartAutoDismiss();
    }

    protected override void OnDetachedFromWindow()
    {
        _dismissVersion++;
        base.OnDetachedFromWindow();
    }

    private void Dismiss(WinoInfoBarCloseReason reason)
    {
        _dismissVersion++;
        Visibility = ViewStates.Gone;
        Closed?.Invoke(this, new WinoInfoBarClosedEventArgs(reason));
    }

    private void UpdateText()
    {
        if (_text is null) return;
        var builder = new SpannableStringBuilder();
        if (!string.IsNullOrWhiteSpace(_title))
        {
            builder.Append(_title);
            builder.SetSpan(new TypefaceSpan("sans-serif-medium"), 0, _title.Length, SpanTypes.ExclusiveExclusive);
            if (!string.IsNullOrWhiteSpace(_message)) builder.Append("  ");
        }
        if (!string.IsNullOrWhiteSpace(_message)) builder.Append(_message);
        _text.TextFormatted = builder;
        ContentDescription = string.Join(". ", new[] { _title, _message }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        RestartAutoDismiss();
    }
}
