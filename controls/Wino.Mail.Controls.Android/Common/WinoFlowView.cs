using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Wino.Mail.Controls.Core.Layout;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// A wrapping row (the AppKit and UIKit WinoFlowView over the shared <see cref="WinoFlowLayout"/>): children keep their
/// measured size and run from the start edge; one that does not fit after another starts a new line. Child margins are
/// part of the item. <see cref="Spacing"/> and <see cref="LineSpacing"/> are in pixels.
/// </summary>
[Register("com.winomail.controls.WinoFlowView")]
public sealed class WinoFlowView : ViewGroup
{
    public WinoFlowView(Context context) : base(context) { }
    public WinoFlowView(Context context, IAttributeSet? attrs) : base(context, attrs) { }
    private WinoFlowView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public int Spacing { get; set; }

    public int LineSpacing { get; set; }

    private WinoFlowLayoutResult? _layout;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int available = MeasureSpec.GetSize(widthMeasureSpec) - PaddingLeft - PaddingRight;
        if (MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Unspecified) available = int.MaxValue / 2;
        var sizes = new List<(double Width, double Height)>();
        foreach (var child in VisibleChildren())
        {
            MeasureChildWithMargins(child, widthMeasureSpec, 0, heightMeasureSpec, 0);
            var margins = child.LayoutParameters as MarginLayoutParams;
            sizes.Add((child.MeasuredWidth + (margins?.LeftMargin ?? 0) + (margins?.RightMargin ?? 0),
                child.MeasuredHeight + (margins?.TopMargin ?? 0) + (margins?.BottomMargin ?? 0)));
        }
        _layout = WinoFlowLayout.Arrange(sizes, available, Spacing, LineSpacing);
        int usedWidth = 0;
        foreach (var frame in _layout.Frames) usedWidth = Math.Max(usedWidth, (int)Math.Ceiling(frame.X + frame.Width));
        int width = MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Exactly ? MeasureSpec.GetSize(widthMeasureSpec) : usedWidth + PaddingLeft + PaddingRight;
        int height = (int)Math.Ceiling(_layout.Height) + PaddingTop + PaddingBottom;
        SetMeasuredDimension(ResolveSize(width, widthMeasureSpec), ResolveSize(height, heightMeasureSpec));
    }

    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        if (_layout is null) return;
        bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
        int width = r - l;
        int index = 0;
        foreach (var child in VisibleChildren())
        {
            if (index >= _layout.Frames.Count) break;
            var frame = _layout.Frames[index++];
            var margins = child.LayoutParameters as MarginLayoutParams;
            int x = (int)frame.X + (margins?.LeftMargin ?? 0);
            int y = PaddingTop + (int)frame.Y + (margins?.TopMargin ?? 0);
            int left = rtl ? width - PaddingRight - x - child.MeasuredWidth : PaddingLeft + x;
            child.Layout(left, y, left + child.MeasuredWidth, y + child.MeasuredHeight);
        }
    }

    private IEnumerable<View> VisibleChildren()
    {
        for (int index = 0; index < ChildCount; index++)
            if (GetChildAt(index) is { Visibility: not ViewStates.Gone } child) yield return child;
    }

    protected override LayoutParams GenerateDefaultLayoutParams() => new MarginLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
    public override LayoutParams GenerateLayoutParams(IAttributeSet? attrs) => new MarginLayoutParams(Context, attrs);
    protected override LayoutParams GenerateLayoutParams(LayoutParams? p) => p is MarginLayoutParams margins ? new MarginLayoutParams(margins) : new MarginLayoutParams(p);
    protected override bool CheckLayoutParams(LayoutParams? p) => p is MarginLayoutParams;
}
