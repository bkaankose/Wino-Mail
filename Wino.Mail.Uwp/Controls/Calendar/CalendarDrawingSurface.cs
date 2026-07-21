using Windows.UI.Xaml.Controls;

namespace Wino.Calendar.Controls;

/// <summary>
/// Placeholder surface for the calendar's optional accelerated background grid.
/// Calendar events, headers, selection and navigation remain XAML controls; only the
/// unsupported SkiaSharp UWP drawing layer is omitted on modern .NET UWP.
/// </summary>
public sealed class CalendarDrawingSurface : Canvas
{
    public void Invalidate()
    {
        // The old SKXamlCanvas invalidation is intentionally unsupported.
    }
}
