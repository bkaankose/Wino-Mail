using Android.Content;
using System.Diagnostics.CodeAnalysis;
using Android.Util;

namespace Wino.Presentation.Android;

/// <summary>dp and sp conversions (WinoTokens are in dp, the Android twin of the UIKit points).</summary>
public static class WinoUnits
{
    public static float Density(Context context) => context.Resources?.DisplayMetrics?.Density ?? 1f;

    /// <summary>Pixels for <paramref name="dp"/>, rounded.</summary>
    public static int Dp(Context context, double dp) => (int)Math.Round(dp * Density(context));

    /// <summary>Pixels for <paramref name="dp"/>, unrounded (radii, strokes).</summary>
    public static float DpF(Context context, double dp) => (float)(dp * Density(context));

    /// <summary>Pixels for <paramref name="sp"/> with the user's font scale.</summary>
    public static float Sp(Context context, double sp)
        => context.Resources?.DisplayMetrics is { } metrics ? TypedValue.ApplyDimension(ComplexUnitType.Sp, (float)sp, metrics) : (float)sp;

    /// <summary>dp for <paramref name="pixels"/>.</summary>
    public static double ToDp(Context context, int pixels) => pixels / (double)Density(context);
}

/// <summary>Small view helpers.</summary>
public static class WinoViews
{
    /// <summary>The view's context; every attached or inflated view has one.</summary>
    public static global::Android.Content.Context RequireContext(this global::Android.Views.View view)
        => view.Context ?? throw new InvalidOperationException($"{view.GetType().Name} has no context.");

    /// <summary>A child view by id, or an exception naming the missing id (layouts are part of the library).</summary>
    public static T Require<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>(this global::Android.Views.View root, int id) where T : global::Android.Views.View
        => root.FindViewById<T>(id) ?? throw new InvalidOperationException($"{root.GetType().Name} has no child view {id}.");
}

/// <summary>Non-null handles for Java enum constants that the bindings expose as nullable.</summary>
public static class WinoGraphics
{
    public static global::Android.Graphics.Shader.TileMode Clamp
        => global::Android.Graphics.Shader.TileMode.Clamp ?? throw new InvalidOperationException("Shader.TileMode.CLAMP is unavailable.");
}
