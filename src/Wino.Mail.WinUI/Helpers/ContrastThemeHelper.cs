using Windows.UI;
using Windows.UI.ViewManagement;

namespace Wino.Mail.WinUI.Helpers;

internal static class ContrastThemeHelper
{
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly UISettings SystemUISettings = new();

    public static bool IsActive => Accessibility.HighContrast;

    public static Color GetSystemColor(UIElementType elementType) => SystemUISettings.UIElementColor(elementType);
}
