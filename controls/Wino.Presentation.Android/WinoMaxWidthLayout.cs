using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Wino.Presentation.Android;

/// <summary>
/// A frame that never grows wider than <see cref="MaxWidthDp"/> (the 560 dp content column of onboarding and dialogs
/// on tablets and landscape phones). Place it with <c>layout_gravity="center_horizontal"</c> to centre the column.
/// Usable from XML as <c>com.winomail.presentation.WinoMaxWidthLayout</c>.
/// </summary>
[Register("com.winomail.presentation.WinoMaxWidthLayout")]
public class WinoMaxWidthLayout : FrameLayout
{
    public WinoMaxWidthLayout(Context context) : base(context) { }
    public WinoMaxWidthLayout(Context context, IAttributeSet? attrs) : base(context, attrs) { }
    public WinoMaxWidthLayout(Context context, IAttributeSet? attrs, int defStyleAttr) : base(context, attrs, defStyleAttr) { }
    protected WinoMaxWidthLayout(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    /// <summary>The widest the column gets, in dp (560 by default, the wino_zone_max_width token).</summary>
    public double MaxWidthDp { get; set; } = 560;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var max = Context is { } context ? WinoUnits.Dp(context, MaxWidthDp) : int.MaxValue;
        var width = MeasureSpec.GetSize(widthMeasureSpec);
        if (MeasureSpec.GetMode(widthMeasureSpec) != MeasureSpecMode.Unspecified && width > max)
            widthMeasureSpec = MeasureSpec.MakeMeasureSpec(max, MeasureSpecMode.Exactly);
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
