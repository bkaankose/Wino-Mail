using Microsoft.UI.Windowing;
using Windows.UI;

namespace Wino.Mail.WinUI.Helpers;

internal static class SystemCaptionButtonColorHelper
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    public static void Apply(AppWindowTitleBar titleBar, bool isDarkTheme)
    {
        if (titleBar == null)
            return;

        var foreground = isDarkTheme
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);
        var inactiveForeground = isDarkTheme
            ? Color.FromArgb(128, 255, 255, 255)
            : Color.FromArgb(128, 0, 0, 0);
        var hoverBackground = isDarkTheme
            ? Color.FromArgb(20, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0);
        var pressedBackground = isDarkTheme
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0);

        titleBar.ButtonBackgroundColor = Transparent;
        titleBar.ButtonInactiveBackgroundColor = Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }
}
