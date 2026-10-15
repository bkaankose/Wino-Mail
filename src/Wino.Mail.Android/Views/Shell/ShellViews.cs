using Android.Content;
using Android.Util;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>Small view helpers the shell and mail pages share.</summary>
internal static class ShellViews
{
    /// <summary>The theme's borderless ripple drawable resource (icon buttons).</summary>
    public static int BorderlessRipple(Context context)
    {
        var value = new TypedValue();
        context.Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackgroundBorderless, value, true);
        return value.ResourceId;
    }

    /// <summary>The theme's bounded ripple drawable resource (rows, tiles).</summary>
    public static int Ripple(Context context)
    {
        var value = new TypedValue();
        context.Theme?.ResolveAttribute(global::Android.Resource.Attribute.SelectableItemBackground, value, true);
        return value.ResourceId;
    }
}
