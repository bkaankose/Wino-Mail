using UIKit;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Settings sheet's chrome, installed by its pages (the sheet's navigation controller belongs to the shell host):
/// one tracked theme backdrop behind every page of the stack, translucent bars over it, and a Done button on the
/// stack's first page that closes Settings through the shared router.
/// </summary>
public static class SettingsSheetChrome
{
    private const int BackdropTag = 0x5E771;

    /// <summary>Closes the Settings sheet and releases its pages (set when services are ready).</summary>
    public static Func<Task>? Close { get; set; }

    public static void Install(UINavigationController? navigation)
    {
        if (navigation?.View is not { } root) return;

        if (root.ViewWithTag(BackdropTag) is null)
        {
            var backdrop = UIKitThemeService.Track(new ThemeBackdropView { Tag = BackdropTag });
            // Fill adds the view on top; send it behind the navigation stack afterwards.
            WinoLayout.Fill(backdrop, root);
            root.SendSubviewToBack(backdrop);
            root.BackgroundColor = UIColor.SystemBackground;

            var scrolled = new UINavigationBarAppearance();
            scrolled.ConfigureWithDefaultBackground();
            var edge = new UINavigationBarAppearance();
            edge.ConfigureWithTransparentBackground();
            navigation.NavigationBar.StandardAppearance = scrolled;
            navigation.NavigationBar.ScrollEdgeAppearance = edge;
            navigation.NavigationBar.CompactAppearance = scrolled;
        }

        if (navigation.ViewControllers is { Length: > 0 } pages && pages[0].NavigationItem is { } first && first.LeftBarButtonItem is null)
        {
            var done = new UIBarButtonItem(UIBarButtonSystemItem.Close, (_, _) => _ = CloseAsync());
            done.AccessibilityLabel = Translator.Buttons_Done;
            first.LeftBarButtonItem = done;
        }
    }

    private static async Task CloseAsync()
    {
        try
        {
            if (Close is { } close) await close();
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not close Settings.");
        }
    }
}
