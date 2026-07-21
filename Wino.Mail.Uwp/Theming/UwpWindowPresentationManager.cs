using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Uwp.Theming;

/// <summary>
/// Owns all single-window presentation setup. The initial theme, backdrop and
/// caption configuration are applied before the window is activated.
/// </summary>
public sealed class UwpWindowPresentationManager
{
    private const string BackdropKey = "WindowBackdropTypeKey";
    private const string LegacyBackdropKey = "BackdropKind";
    private const string ThemeKey = "RootTheme";
    private const string CurrentThemeKey = "CurrentApplicationThemeId";
    private const string CustomThemeFolderName = "CustomThemes";

    private readonly ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
    private Frame? rootFrame;
    private Control? titleBarElement;
    private CoreApplicationViewTitleBar? coreTitleBar;
    private Guid? appliedWallpaperThemeId;

    public Frame? RootFrame => rootFrame;

    public void Prepare(Window window, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(frame);

        rootFrame = frame;
        frame.RequestedTheme = ToElementTheme(ReadRootTheme());

        coreTitleBar ??= CoreApplication.GetCurrentView().TitleBar;
        coreTitleBar.ExtendViewIntoTitleBar = true;

        if (!TryApplyCustomWallpaper(frame))
        {
            ApplyBackdrop(frame, ReadBackdrop());
        }

        ApplyCaptionButtonColors(frame.ActualTheme);
        frame.ActualThemeChanged -= RootFrameActualThemeChanged;
        frame.ActualThemeChanged += RootFrameActualThemeChanged;
    }

    public void RegisterTitleBar(Control element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (titleBarElement is not null && coreTitleBar is not null)
        {
            coreTitleBar.LayoutMetricsChanged -= CoreTitleBarLayoutMetricsChanged;
        }

        titleBarElement = element;
        coreTitleBar ??= CoreApplication.GetCurrentView().TitleBar;
        coreTitleBar.ExtendViewIntoTitleBar = true;
        coreTitleBar.LayoutMetricsChanged += CoreTitleBarLayoutMetricsChanged;
        ApplySystemButtonInsets();
    }

    public void ApplyRegisteredTitleBar()
    {
        if (titleBarElement is null)
        {
            return;
        }

        // WinoAppShell is constructed while its Frame is detached. UWP only accepts
        // a title-bar drag region once that element belongs to Window.Content.
        Window.Current.SetTitleBar(titleBarElement);
        ApplySystemButtonInsets();
    }

    public void ApplyRootTheme(ApplicationElementTheme theme)
    {
        if (rootFrame is null)
        {
            return;
        }

        rootFrame.RequestedTheme = ToElementTheme(theme);
        ApplyCaptionButtonColors(rootFrame.ActualTheme);
    }

    /// <summary>
    /// True when the given wallpaper theme is already painting the root frame. The
    /// pre-activation Prepare pass applies the persisted wallpaper; re-applying the
    /// same image after first render would flash the backdrop while a new
    /// BitmapImage decodes.
    /// </summary>
    public bool IsWallpaperApplied(Guid themeId) => appliedWallpaperThemeId == themeId;

    public void ApplyBackdrop(UwpBackdropKind kind, ImageBrush? imageBrush = null, Guid? wallpaperThemeId = null)
    {
        appliedWallpaperThemeId = kind == UwpBackdropKind.Image ? wallpaperThemeId : null;
        if (rootFrame is not null)
        {
            ApplyBackdrop(rootFrame, kind, imageBrush);
        }
    }

    public void ApplyCaptionButtonColors(ElementTheme theme)
    {
        var isDark = theme == ElementTheme.Dark ||
                     theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var foreground = isDark ? Colors.White : Colors.Black;
        var inactiveForeground = isDark
            ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x99, 0x00, 0x00, 0x00);

        var titleBar = ApplicationView.GetForCurrentView().TitleBar;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedForegroundColor = Colors.White;
    }

    public UwpBackdropKind NormalizePersistedBackdrop(string? persisted) => persisted switch
    {
        "0" or "None" => UwpBackdropKind.Solid,
        "1" or "2" or "Mica" or "MicaAlt" => UwpBackdropKind.Mica,
        "3" or "4" or "5" or "DesktopAcrylic" or "AcrylicBase" or "AcrylicThin" => UwpBackdropKind.Acrylic,
        _ when Enum.TryParse<UwpBackdropKind>(persisted, true, out var value) => value,
        _ => UwpBackdropKind.Mica,
    };

    private UwpBackdropKind ReadBackdrop()
    {
        var persisted = settings.Values.TryGetValue(BackdropKey, out var current)
            ? current?.ToString()
            : settings.Values.TryGetValue(LegacyBackdropKey, out var legacy)
                ? legacy?.ToString()
                : null;
        var normalized = NormalizePersistedBackdrop(persisted);
        settings.Values[LegacyBackdropKey] = normalized.ToString();
        return normalized;
    }

    private ApplicationElementTheme ReadRootTheme() =>
        Enum.TryParse<ApplicationElementTheme>(settings.Values[ThemeKey]?.ToString(), out var value)
            ? value
            : ApplicationElementTheme.Default;

    private bool TryApplyCustomWallpaper(Frame frame)
    {
        if (!Guid.TryParse(settings.Values[CurrentThemeKey]?.ToString(), out var themeId) || themeId == Guid.Empty)
        {
            return false;
        }

        var imagePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            CustomThemeFolderName,
            $"{themeId}_preview.jpg");
        if (!File.Exists(imagePath))
        {
            return false;
        }

        ApplyBackdrop(frame, UwpBackdropKind.Image, new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri($"ms-appdata:///local/{CustomThemeFolderName}/{themeId}_preview.jpg")),
            Stretch = Stretch.UniformToFill,
        });
        appliedWallpaperThemeId = themeId;
        return true;
    }

    private static ElementTheme ToElementTheme(ApplicationElementTheme theme) => theme switch
    {
        ApplicationElementTheme.Light => ElementTheme.Light,
        ApplicationElementTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static void ApplyBackdrop(Frame frame, UwpBackdropKind kind, ImageBrush? imageBrush = null)
    {
        BackdropMaterial.SetApplyToRootOrPageBackground(frame, kind == UwpBackdropKind.Mica);
        frame.Background = kind switch
        {
            UwpBackdropKind.Mica => new SolidColorBrush(Colors.Transparent),
            UwpBackdropKind.Acrylic => Application.Current.Resources["WinoAcrylicBackdropBrush"] as Brush,
            UwpBackdropKind.Image when imageBrush is not null => imageBrush,
            _ => Application.Current.Resources["WinoSolidBackdropBrush"] as Brush,
        };
    }

    private void RootFrameActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyCaptionButtonColors(sender.ActualTheme);

    private void CoreTitleBarLayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args) =>
        ApplySystemButtonInsets();

    private void ApplySystemButtonInsets()
    {
        if (titleBarElement is null || coreTitleBar is null)
        {
            return;
        }

        var padding = titleBarElement.Padding;
        titleBarElement.Padding = new Thickness(
            coreTitleBar.SystemOverlayLeftInset,
            padding.Top,
            coreTitleBar.SystemOverlayRightInset,
            padding.Bottom);
    }
}
