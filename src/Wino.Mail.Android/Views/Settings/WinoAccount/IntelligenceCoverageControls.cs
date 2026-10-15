using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>Coverage band colours shared by the histogram bars and the band tiles.</summary>
internal static class CoverageColors
{
    public static Color Indexed(Context context) => WinoTokens.Color(context, Resource.Color.wino_winoaccount_coverage_indexed);
    public static Color ToIndex(Context context) => WinoColors.Accent(context);
    public static Color Outside(Context context) => WinoTokens.Color(context, Resource.Color.wino_winoaccount_coverage_outside);
}

/// <summary>
/// The coverage histogram (Windows CoverageBucketTemplate in an ItemsRepeater, iOS CoverageHistogramView): one column per
/// bucket in the ViewModel's order, left to right, each stacking indexed (bottom), will be indexed, then left out.
/// </summary>
internal sealed class CoverageHistogramView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private IReadOnlyList<SemanticIndexRangeBucketViewModel> _buckets = [];

    public CoverageHistogramView(Context context) : base(context)
    {
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    public void SetBuckets(IReadOnlyList<SemanticIndexRangeBucketViewModel> buckets)
    {
        _buckets = buckets;
        Invalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var context = Context;
        // Bar heights are ViewModel data in dp (the shared histogram scale), not a page style.
        int height = context is null ? 0 : (int)Math.Round(SemanticIndexRangeBucketViewModel.MaximumBarHeight * WinoUnits.Density(context));
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), height);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_buckets.Count == 0 || Context is not { } context) return;
        float gap = WinoTokens.PxF(context, Resource.Dimension.wino_space_3xs);
        float width = Math.Max(1, (Width - gap * (_buckets.Count - 1)) / _buckets.Count);
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_space_hairline);
        float density = WinoUnits.Density(context);
        for (int i = 0; i < _buckets.Count; i++)
        {
            var bucket = _buckets[i];
            float x = i * (width + gap);
            // Segments grow up from the bottom edge.
            float y = Height;
            void Segment(double heightDp, Color color, bool top)
            {
                if (heightDp <= 0) return;
                float height = (float)(heightDp * density);
                _paint.Color = color;
                y -= height;
                var bar = new RectF(x, y, x + width, y + height);
                if (top) canvas.DrawRoundRect(bar, radius, radius, _paint);
                else canvas.DrawRect(bar, _paint);
            }
            Segment(bucket.IndexedHeight, CoverageColors.Indexed(context), bucket.SelectedNotIndexedHeight <= 0 && bucket.OutsideHeight <= 0);
            Segment(bucket.SelectedNotIndexedHeight, CoverageColors.ToIndex(context), bucket.OutsideHeight <= 0);
            Segment(bucket.OutsideHeight, CoverageColors.Outside(context), true);
        }
    }
}

/// <summary>
/// One row of the folder tree (the Mac outline cell, iOS CoverageFolderCell): indent by depth (one spacing step per level), glyph, name (bold when
/// selected), included dot and message count, with the selection fill and the accent bar. Tapping selects it.
/// </summary>
internal sealed class CoverageFolderCell : FrameLayout
{
    private readonly View _selection;
    private readonly View _bar;
    private readonly TextView _name;
    private readonly View _dot;
    private readonly TextView _count;
    private bool _isCurrent;

    public CoverageFolderCell(Context context, IntelligenceFolderNode node, int depth) : base(context)
    {
        Node = node;
        Clickable = true;
        Focusable = true;
        SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large));
        var ripple = new global::Android.Util.TypedValue();
        if (context.Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackground, ripple, true) == true && OperatingSystem.IsAndroidVersionAtLeast(23))
            Foreground = context.GetDrawable(ripple.ResourceId);

        _selection = new View(context) { Background = IntelligenceViews.Shape(context, WinoColors.SelectedFill(context), null, Resource.Dimension.wino_radius_medium) };
        int inset = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs), vertical = WinoTokens.Px(context, Resource.Dimension.wino_space_hairline);
        AddView(_selection, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent) { LeftMargin = inset, RightMargin = inset, TopMargin = vertical, BottomMargin = vertical });
        _bar = new View(context) { Background = IntelligenceViews.Shape(context, WinoColors.Accent(context), null, Resource.Dimension.wino_radius_pill) };
        AddView(_bar, new LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_folder_bar_width), WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_folder_bar_height), GravityFlags.Start | GravityFlags.CenterVertical) { LeftMargin = inset });

        var icon = new WinoIconView(context) { Icon = WinoIconGlyph.Folder, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m) };
        _name = IntelligenceViews.Label(context, string.Empty, WinoTextStyle.Body, maxLines: 1);
        _dot = new View(context) { Background = IntelligenceViews.Oval(context, WinoColors.Accent(context)) };
        int dotSize = WinoTokens.Px(context, Resource.Dimension.wino_size_dot);
        _dot.LayoutParameters = new LinearLayout.LayoutParams(dotSize, dotSize);
        _count = IntelligenceViews.Label(context, string.Empty, WinoTextStyle.Caption);
        _count.Gravity = GravityFlags.End;
        _count.SetMinWidth(WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_folder_count_min));
        var row = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_s, _name, icon, _name, _dot, _count);
        int edge = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        row.SetPadding(edge + depth * WinoTokens.Px(context, Resource.Dimension.wino_space_ml), 0, edge, 0);
        AddView(row, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterVertical));
        for (int index = 0; index < ChildCount; index++)
            if (GetChildAt(index) is { } child) child.ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        Click += (_, _) => Picked?.Invoke(this, EventArgs.Empty);
    }

    public IntelligenceFolderNode Node { get; }

    /// <summary>Raised when the user taps the row.</summary>
    public event EventHandler? Picked;

    public void Configure(bool current)
    {
        _isCurrent = current;
        _name.Text = Node.DisplayName;
        WinoTokens.Style(_name, current ? WinoTextStyle.BodyStrong : WinoTextStyle.Body);
        _dot.Visibility = Node.IsIncluded ? ViewStates.Visible : ViewStates.Invisible;
        _count.Text = Node.AvailableMessageCountText;
        ContentDescription = string.IsNullOrWhiteSpace(Node.CoverageBadgeTooltip) ? Node.RowAutomationName : $"{Node.RowAutomationName}. {Node.CoverageBadgeTooltip}";
        _selection.Visibility = current ? ViewStates.Visible : ViewStates.Gone;
        _bar.Visibility = current ? ViewStates.Visible : ViewStates.Gone;
    }

    public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? info)
    {
        base.OnInitializeAccessibilityNodeInfo(info);
        if (info is null) return;
        info.ClassName = "android.widget.Button";
        info.Selected = _isCurrent;
    }
}
