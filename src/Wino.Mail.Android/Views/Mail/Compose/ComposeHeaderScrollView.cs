using Android.Content;
using Android.Runtime;
using Android.Util;
using AndroidX.Core.Widget;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The composer header (From, recipients, Subject, attachments): as tall as its content, but never more than
/// <c>wino_compose_header_max_share</c> of the height its parent offers, so the message body keeps room when the keyboard is open
/// or the phone is in landscape; the header then scrolls on its own.
/// </summary>
[Register("com.winomail.android.ComposeHeaderScrollView")]
public sealed class ComposeHeaderScrollView : NestedScrollView
{

    public ComposeHeaderScrollView(Context context) : base(context) { }
    public ComposeHeaderScrollView(Context context, IAttributeSet? attrs) : base(context, attrs) { }
    private ComposeHeaderScrollView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var mode = MeasureSpec.GetMode(heightMeasureSpec);
        if (mode != global::Android.Views.MeasureSpecMode.Unspecified && Context is { } context)
        {
            int cap = (int)(MeasureSpec.GetSize(heightMeasureSpec) * ComposeStyle.Float(context, Resource.Dimension.wino_compose_header_max_share));
            heightMeasureSpec = MeasureSpec.MakeMeasureSpec(cap, global::Android.Views.MeasureSpecMode.AtMost);
        }
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
