using Android.Content;
using Android.Graphics;
using Android.Widget;
using AndroidX.Core.Widget;

namespace Wino.Presentation.Android;

/// <summary>Text appearances of code-built views (Resources/values/wino_text.xml).</summary>
public enum WinoTextStyle
{
    Tiny,
    Caption,
    CaptionStrong,
    Description,
    Body,
    BodySecondary,
    BodyStrong,
    BodyLarge,
    BodyLargeStrong,
    Subtitle,
    Title,
    Display,
    TopAppBarTitle,
    Subheader,
    ItemTitle,
    ItemSupporting,
    Label
}

/// <summary>
/// The design tokens for views built in code (Settings pages, form dialogs): sizes from Resources/values/wino_scale.xml,
/// colours from the wino_* colour resources (light and night) and text appearances from wino_text.xml. Code passes
/// resource ids, never literal sizes or colours, so a restyle changes resources only.
/// </summary>
public static class WinoTokens
{
    /// <summary>A dimension token in pixels.</summary>
    public static int Px(Context context, int dimenResource)
        => context.Resources?.GetDimensionPixelSize(dimenResource) ?? 0;

    /// <summary>A dimension token in pixels as a float (radii, strokes).</summary>
    public static float PxF(Context context, int dimenResource)
        => context.Resources?.GetDimension(dimenResource) ?? 0f;

    /// <summary>A dimension token in dp (APIs that take dp, such as WinoIcons glyph sizes).</summary>
    public static double Dp(Context context, int dimenResource)
        => PxF(context, dimenResource) / WinoUnits.Density(context);

    /// <summary>A colour token for the context's appearance.</summary>
    public static Color Color(Context context, int colorResource) => WinoColors.FromResource(context, colorResource);

    /// <summary>The style resource of a text appearance.</summary>
    public static int StyleResource(WinoTextStyle style) => style switch
    {
        WinoTextStyle.Tiny => Resource.Style.TextAppearance_WinoKit_Tiny,
        WinoTextStyle.Caption => Resource.Style.TextAppearance_WinoKit_Caption,
        WinoTextStyle.CaptionStrong => Resource.Style.TextAppearance_WinoKit_CaptionStrong,
        WinoTextStyle.Description => Resource.Style.TextAppearance_WinoKit_Description,
        WinoTextStyle.BodySecondary => Resource.Style.TextAppearance_WinoKit_BodySecondary,
        WinoTextStyle.BodyStrong => Resource.Style.TextAppearance_WinoKit_BodyStrong,
        WinoTextStyle.BodyLarge => Resource.Style.TextAppearance_WinoKit_BodyLarge,
        WinoTextStyle.BodyLargeStrong => Resource.Style.TextAppearance_WinoKit_BodyLargeStrong,
        WinoTextStyle.Subtitle => Resource.Style.TextAppearance_WinoKit_Subtitle,
        WinoTextStyle.Title => Resource.Style.TextAppearance_WinoKit_Title,
        WinoTextStyle.Display => Resource.Style.TextAppearance_WinoKit_Display,
        WinoTextStyle.TopAppBarTitle => Resource.Style.TextAppearance_WinoKit_TopAppBarTitle,
        WinoTextStyle.Subheader => Resource.Style.TextAppearance_WinoKit_Subheader,
        WinoTextStyle.ItemTitle => Resource.Style.TextAppearance_WinoKit_ItemTitle,
        WinoTextStyle.ItemSupporting => Resource.Style.TextAppearance_WinoKit_ItemSupporting,
        WinoTextStyle.Label => Resource.Style.TextAppearance_WinoKit_Label,
        _ => Resource.Style.TextAppearance_WinoKit_Body
    };

    /// <summary>Applies a text appearance (size, weight, colour) to <paramref name="view"/>.</summary>
    public static T Style<T>(T view, WinoTextStyle style) where T : TextView
    {
        TextViewCompat.SetTextAppearance(view, StyleResource(style));
        return view;
    }

    /// <summary>Padding from dimension tokens (start, top, end, bottom; 0 for none).</summary>
    public static void Pad(global::Android.Views.View view, int start, int top, int end, int bottom)
    {
        var context = view.Context ?? throw new InvalidOperationException("The view has no context.");
        int Read(int resource) => resource == 0 ? 0 : Px(context, resource);
        view.SetPaddingRelative(Read(start), Read(top), Read(end), Read(bottom));
    }
}
