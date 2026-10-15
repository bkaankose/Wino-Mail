using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Wino.Presentation.Android;

/// <summary>
/// The wallpaper the theme service chose for the current appearance (UIKit <c>WinoStyle.HasBackdrop</c> and
/// <c>ThemeBackdropView.SetBackdrop</c>). The theme service decodes the bitmap and calls <see cref="Set"/> on the UI
/// thread; <see cref="WinoBackdropView"/> and <see cref="WinoBarBackdropView"/> follow it.
/// </summary>
public static class WinoBackdrop
{
    public static Bitmap? Wallpaper { get; private set; }

    public static bool HasWallpaper => Wallpaper is not null;

    /// <summary>Raised on the UI thread after the wallpaper changes.</summary>
    public static event EventHandler? Changed;

    public static void Set(Bitmap? wallpaper)
    {
        if (ReferenceEquals(Wallpaper, wallpaper)) return;
        Wallpaper = wallpaper;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The window-aligned centre-crop matrix for <paramref name="bitmap"/> in a window of the given size, shifted by
    /// the view's offset in the window, so a slice drawn by a bar lines up with the full-window wallpaper.
    /// </summary>
    internal static Matrix CropMatrix(Bitmap bitmap, int windowWidth, int windowHeight, int offsetX, int offsetY)
    {
        float scale = Math.Max(windowWidth / (float)bitmap.Width, windowHeight / (float)bitmap.Height);
        var matrix = new Matrix();
        matrix.SetScale(scale, scale);
        matrix.PostTranslate((windowWidth - bitmap.Width * scale) / 2 - offsetX, (windowHeight - bitmap.Height * scale) / 2 - offsetY);
        return matrix;
    }
}

/// <summary>
/// The full-window theme backdrop (Windows WinoApplicationBackgroundColor): the wallpaper centre-cropped edge to edge
/// under the system bars, or the backdrop token colour without one. Place it first in the activity's root layout.
/// </summary>
[Register("com.winomail.presentation.WinoBackdropView")]
public class WinoBackdropView : ImageView
{
    public WinoBackdropView(Context context) : base(context) => Initialize();
    public WinoBackdropView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    protected WinoBackdropView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        SetScaleType(ScaleType.CenterCrop);
        ImportantForAccessibility = ImportantForAccessibility.No;
        SetBackgroundColor(WinoColors.FromResource(this.RequireContext(), Resource.Color.wino_backdrop));
        Refresh();
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoBackdrop.Changed += BackdropChanged;
        Refresh();
    }

    protected override void OnDetachedFromWindow()
    {
        WinoBackdrop.Changed -= BackdropChanged;
        base.OnDetachedFromWindow();
    }

    private void BackdropChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh() => SetImageBitmap(WinoBackdrop.Wallpaper);
}

/// <summary>
/// The backdrop of a bar over the wallpaper (UIKit <c>WinoBarBackdropView</c>, canvas Tokens "Bar tint"): the slice of
/// the wallpaper under the bar, blurred with a 30 px RenderEffect on API 31+, under the translucent bar tint. Below
/// API 31, or without a wallpaper, only the tint shows. Put it behind the toolbar content (first child of the bar).
/// </summary>
[Register("com.winomail.presentation.WinoBarBackdropView")]
public class WinoBarBackdropView : View
{
    private const float BlurRadius = 30;
    private readonly Paint _bitmapPaint = new(PaintFlags.FilterBitmap);
    private readonly int[] _location = new int[2];

    public WinoBarBackdropView(Context context) : base(context) => Initialize();
    public WinoBarBackdropView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    protected WinoBarBackdropView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        ImportantForAccessibility = ImportantForAccessibility.No;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            SetRenderEffect(RenderEffect.CreateBlurEffect(BlurRadius, BlurRadius, WinoGraphics.Clamp));
    }

    /// <summary>True when the wallpaper slice is blurred (API 31+).</summary>
    public static bool IsBlurSupported => OperatingSystem.IsAndroidVersionAtLeast(31);

    /// <summary>
    /// The backdrop never sizes its bar: it is 0 unless the bar gives it an exact size, so a wrap-content bar measures
    /// its content and the backdrop then fills it (FrameLayout re-measures match-parent children exactly).
    /// </summary>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        => SetMeasuredDimension(ResolveSize(0, widthMeasureSpec), ResolveSize(0, heightMeasureSpec));

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        WinoBackdrop.Changed += BackdropChanged;
    }

    protected override void OnDetachedFromWindow()
    {
        WinoBackdrop.Changed -= BackdropChanged;
        base.OnDetachedFromWindow();
    }

    private void BackdropChanged(object? sender, EventArgs args) => Invalidate();

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Context is not { } context) return;
        if (IsBlurSupported && WinoBackdrop.Wallpaper is { } bitmap && RootView is { } root && root.Width > 0)
        {
            GetLocationInWindow(_location);
            root.GetLocationInWindow(_rootLocation);
            canvas.DrawBitmap(bitmap, WinoBackdrop.CropMatrix(bitmap, root.Width, root.Height, _location[0] - _rootLocation[0], _location[1] - _rootLocation[1]), _bitmapPaint);
        }
        canvas.DrawColor(WinoColors.BarTint(context));
    }

    private readonly int[] _rootLocation = new int[2];
}
