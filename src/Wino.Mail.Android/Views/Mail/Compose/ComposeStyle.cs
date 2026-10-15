using Android.Content;
using AndroidX.Core.Content.Resources;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// Reads the composer's visual values from resources (dimens_compose.xml): glyph sizes in dp for WinoIcons, sizes in
/// pixels, and the float values (alphas, shares). The compose code holds no visual literals of its own.
/// </summary>
internal static class ComposeStyle
{
    /// <summary>A dimension in dp (glyph sizes for WinoIcons and WinoContactPicture).</summary>
    public static double Dp(Context context, int dimension)
        => context.Resources is { DisplayMetrics: { } metrics } resources ? resources.GetDimension(dimension) / metrics.Density : 0;

    /// <summary>A dimension in pixels.</summary>
    public static int Px(Context context, int dimension) => context.Resources?.GetDimensionPixelSize(dimension) ?? 0;

    /// <summary>A float value (<c>format="float" type="dimen"</c>).</summary>
    public static float Float(Context context, int dimension)
        => context.Resources is { } resources ? ResourcesCompat.GetFloat(resources, dimension) : 1f;
}
