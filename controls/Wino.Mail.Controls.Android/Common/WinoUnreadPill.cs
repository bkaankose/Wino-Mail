using Android.Content;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// The count badge (canvas Components "count badge"; UIKit <c>WinoUnreadPill</c>): an accent pill with the count in
/// on-accent text, or with <see cref="IsSubtle"/> the plain secondary count used by unselected folder rows. Hidden at 0.
/// Usable from XML as <c>com.winomail.controls.WinoUnreadPill</c>.
/// </summary>
[Register("com.winomail.controls.WinoUnreadPill")]
public class WinoUnreadPill : TextView
{
    private int _count;
    private bool _isSubtle;

    public WinoUnreadPill(Context context) : base(context) => Initialize();
    public WinoUnreadPill(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    protected WinoUnreadPill(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        Gravity = GravityFlags.Center;
        SetIncludeFontPadding(false);
        SetSingleLine(true);
        Refresh();
    }

    public int Count
    {
        get => _count;
        set { _count = value; Refresh(); }
    }

    /// <summary>Plain secondary text without the accent fill.</summary>
    public bool IsSubtle
    {
        get => _isSubtle;
        set { _isSubtle = value; Refresh(); }
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoColors.AccentChanged += AccentChanged;
        Refresh();
    }

    protected override void OnDetachedFromWindow()
    {
        WinoColors.AccentChanged -= AccentChanged;
        base.OnDetachedFromWindow();
    }

    private void AccentChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (Context is not { } context) return;
        Visibility = _count > 0 ? ViewStates.Visible : ViewStates.Gone;
        Text = WinoControlText.UnreadPill(_count);
        if (_isSubtle)
        {
            Background = null;
            SetMinWidth(0);
            SetMinHeight(0);
            SetPadding(0, 0, 0, 0);
            SetTextSize(ComplexUnitType.Sp, 13);
            Typeface = global::Android.Graphics.Typeface.Default;
            SetTextColor(WinoColors.SecondaryText(context));
            return;
        }
        var pill = new GradientDrawable();
        pill.SetColor(WinoColors.Accent(context));
        pill.SetCornerRadius(WinoUnits.DpF(context, 10));
        Background = pill;
        SetMinWidth(WinoUnits.Dp(context, 22));
        SetMinHeight(WinoUnits.Dp(context, 20));
        int horizontal = WinoUnits.Dp(context, 7);
        SetPadding(horizontal, 0, horizontal, 0);
        SetTextSize(ComplexUnitType.Sp, 11);
        Typeface = global::Android.Graphics.Typeface.Create("sans-serif-medium", global::Android.Graphics.TypefaceStyle.Normal);
        SetTextColor(WinoColors.OnAccent(context));
    }
}
