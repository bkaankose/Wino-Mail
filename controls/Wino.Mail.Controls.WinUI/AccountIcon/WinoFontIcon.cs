using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Mail.Controls.AccountIcon;

/// <summary>
/// Font icon backed by Wino's packaged icon font.
/// </summary>
public partial class WinoFontIcon : FontIcon
{
    internal const string FontFamilyUri = "ms-appx:///Wino.Mail.Controls.WinUI/Assets/WinoIcons.ttf#WinoIcons";

    public WinoFontIcon()
    {
        FontFamily = WinoFontIconFamily.Current;
        WinoFontIconFamily.Track(this);
    }
}

/// <summary>
/// Shareable icon source backed by Wino's packaged icon font.
/// </summary>
public partial class WinoFontIconSource : FontIconSource
{
    public WinoFontIconSource()
    {
        FontFamily = WinoFontIconFamily.Current;
        WinoFontIconFamily.Track(this);
    }

    protected override IconElement CreateIconElementCore() => new WinoFontIcon();
}

/// <summary>
/// The monochrome font every <see cref="WinoFontIcon"/> and <see cref="WinoFontIconSource"/> draws with. It is the
/// library's own copy of the Classic font until the app points it at the monochrome font of the user's icon set;
/// <see cref="Set"/> then updates the live icons too. Call it on the UI thread.
/// </summary>
public static class WinoFontIconFamily
{
    private static readonly List<WeakReference<FontIcon>> s_icons = [];
    private static readonly List<WeakReference<FontIconSource>> s_sources = [];

    public static FontFamily Current { get; private set; } = new(WinoFontIcon.FontFamilyUri);

    /// <summary>Switches every live and future icon to <paramref name="fontFamilySource"/> (an ms-appx font URI with its family).</summary>
    public static void Set(string fontFamilySource)
    {
        if (Current.Source == fontFamilySource) return;

        Current = new FontFamily(fontFamilySource);

        s_icons.RemoveAll(reference => !reference.TryGetTarget(out _));
        s_sources.RemoveAll(reference => !reference.TryGetTarget(out _));

        foreach (var reference in s_icons)
        {
            if (reference.TryGetTarget(out var icon)) icon.FontFamily = Current;
        }

        foreach (var reference in s_sources)
        {
            if (reference.TryGetTarget(out var source)) source.FontFamily = Current;
        }
    }

    /// <summary>Keeps <paramref name="icon"/> on the current font when it changes.</summary>
    public static void Track(FontIcon icon) => Add(s_icons, icon);

    /// <summary>Keeps <paramref name="source"/> on the current font when it changes.</summary>
    public static void Track(FontIconSource source) => Add(s_sources, source);

    // Recycled list rows create icons all the time, so collected ones are dropped as the list grows.
    private static void Add<T>(List<WeakReference<T>> references, T target) where T : class
    {
        if (references.Count >= 512 && references.Count % 512 == 0)
            references.RemoveAll(reference => !reference.TryGetTarget(out _));

        references.Add(new WeakReference<T>(target));
    }
}
