using Android.App;
using Android.OS;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Material window width classes (plan section 1): compact phones in portrait use one pane, medium windows (landscape
/// phones, small tablets) a rail with list and reader, expanded windows the Windows layout. A window at least 840 dp wide
/// but under 480 dp tall (every phone in landscape; canvas decision A3) stays medium: the Windows layout needs height
/// for its pane. Layout resources follow the same rule (layout-w600dp, layout-w840dp-h480dp).
/// </summary>
public enum WindowWidthClass
{
    /// <summary>Narrower than 600 dp.</summary>
    Compact,

    /// <summary>600 dp to 839 dp.</summary>
    Medium,

    /// <summary>840 dp and wider.</summary>
    Expanded
}

/// <summary>The width class of the activity window, for shells and pages that choose a layout per class.</summary>
public interface IWindowSizeClassService
{
    WindowWidthClass WidthClass { get; }

    /// <summary>The window width in density-independent pixels.</summary>
    int WidthDp { get; }

    /// <summary>The window height in density-independent pixels.</summary>
    int HeightDp { get; }

    /// <summary>Raised on the UI thread when the width class changes (rotation, multi-window resize, unfolding).</summary>
    event EventHandler<WindowWidthClass>? WidthClassChanged;
}

/// <summary>
/// Measures the activity window: the current window metrics on API 30 and later, the configuration's window size in dp
/// before that. <see cref="MainActivity"/> updates it whenever it is created or its configuration changes.
/// </summary>
public sealed class AndroidWindowSizeClassService : IWindowSizeClassService
{
    public const int MediumMinWidthDp = 600;
    public const int ExpandedMinWidthDp = 840;
    public const int ExpandedMinHeightDp = 480;

    public WindowWidthClass WidthClass { get; private set; } = WindowWidthClass.Compact;
    public int WidthDp { get; private set; }
    public int HeightDp { get; private set; }

    public event EventHandler<WindowWidthClass>? WidthClassChanged;

    public static WindowWidthClass Classify(int widthDp, int heightDp = int.MaxValue) => widthDp switch
    {
        >= ExpandedMinWidthDp when heightDp >= ExpandedMinHeightDp => WindowWidthClass.Expanded,
        >= MediumMinWidthDp => WindowWidthClass.Medium,
        _ => WindowWidthClass.Compact
    };

    /// <summary>Measures <paramref name="activity"/>'s window. Main thread.</summary>
    public void Update(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var resources = activity.Resources;
        float density = resources?.DisplayMetrics?.Density is > 0 and var value ? value : 1f;
        int width, height;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && activity.WindowManager?.CurrentWindowMetrics.Bounds is { } bounds)
        {
            width = (int)Math.Round(bounds.Width() / density);
            height = (int)Math.Round(bounds.Height() / density);
        }
        else
        {
            var configuration = resources?.Configuration;
            width = configuration?.ScreenWidthDp ?? 0;
            height = configuration?.ScreenHeightDp ?? 0;
        }

        WidthDp = width;
        HeightDp = height;
        var widthClass = Classify(width, height);
        if (widthClass == WidthClass) return;
        WidthClass = widthClass;
        WidthClassChanged?.Invoke(this, widthClass);
    }
}
