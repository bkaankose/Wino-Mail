using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using Google.Android.Material.Shape;

namespace Wino.Presentation.Android;

/// <summary>
/// The Wino zone (UIKit <c>WinoZoneView</c>): one rounded 10 dp surface over the wallpaper with a hairline stroke and a
/// small elevation, clipping its content to the rounded shape. The fill follows the custom theme's Workspace (or
/// ReadingPane, <see cref="Surface"/>) colour. Usable from XML as <c>com.winomail.presentation.WinoZoneLayout</c>.
/// </summary>
[Register("com.winomail.presentation.WinoZoneLayout")]
public class WinoZoneLayout : FrameLayout
{
    private readonly MaterialShapeDrawable _shape = new();
    private WinoThemeSurface _surface = WinoThemeSurface.Workspace;
    private double _radiusDp = 10;

    public WinoZoneLayout(Context context) : base(context) => Initialize();
    public WinoZoneLayout(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    public WinoZoneLayout(Context context, IAttributeSet? attrs, int defStyleAttr) : base(context, attrs, defStyleAttr) => Initialize();
    protected WinoZoneLayout(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        Background = _shape;
        ClipToOutline = true;
        Elevation = WinoUnits.DpF(this.RequireContext(), 2);
        Refresh();
    }

    /// <summary>Which custom theme surface recolours the zone: Workspace (default) or ReadingPane.</summary>
    public WinoThemeSurface Surface
    {
        get => _surface;
        set { _surface = value; Refresh(); }
    }

    /// <summary>Corner radius in dp (10 by default; 0 for a zone that fills a pane edge to edge).</summary>
    public double CornerRadiusDp
    {
        get => _radiusDp;
        set { _radiusDp = value; Refresh(); }
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoThemeSurfaces.Changed += ThemeChanged;
        Refresh();
    }

    protected override void OnDetachedFromWindow()
    {
        WinoThemeSurfaces.Changed -= ThemeChanged;
        base.OnDetachedFromWindow();
    }

    private void ThemeChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (Context is not { } context) return;
        bool dark = WinoColors.IsDark(context);
        var fill = WinoThemeSurfaces.Resolve(_surface, dark) ?? WinoColors.ZoneFill(context);
        _shape.ShapeAppearanceModel = ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(WinoUnits.DpF(context, _radiusDp)).Build();
        _shape.FillColor = global::Android.Content.Res.ColorStateList.ValueOf(fill);
        _shape.SetStroke(WinoUnits.DpF(context, 1), WinoColors.ZoneStroke(context));
        Invalidate();
    }
}
