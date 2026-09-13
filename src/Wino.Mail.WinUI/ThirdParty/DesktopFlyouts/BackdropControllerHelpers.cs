// Copyright (c) 0x5BFA. All rights reserved.
// Licensed under the MIT license.

using Windows.UI;


using Microsoft.UI.Composition.SystemBackdrops;
using Windows.UI.ViewManagement;


namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;


internal static class BackdropControllerHelpers
{
    private static UISettings UISettings => field ??= new UISettings();

    internal static MicaController? GetMicaController(SystemBackdropTheme theme)
    {
        if (!MicaController.IsSupported())
            return null;

        if (GeneralHelpers.IsTaskbarColorPrevalenceEnabled())
            return GetAccentedMicaController();

        return IsLightTheme(theme)
            ? GetLightMicaController()
            : GetDarkMicaController();
    }

    internal static MicaController? GetDarkMicaController()
    {
        return new MicaController()
        {
            FallbackColor = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C),
            LuminosityOpacity = 0.96F,
            TintColor = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            TintOpacity = 0.5F,
        };
    }

    internal static MicaController? GetLightMicaController()
    {
        return new MicaController()
        {
            FallbackColor = Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE),
            LuminosityOpacity = 0.9F,
            TintColor = Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3),
            TintOpacity = 0.0F,
        };
    }

    internal static MicaController? GetAccentedMicaController()
    {
        var systemAccentColorDark2 = UISettings.GetColorValue(UIColorType.AccentDark2);
        return new MicaController()
        {
            FallbackColor = systemAccentColorDark2,
            LuminosityOpacity = 0.8F,
            TintColor = systemAccentColorDark2,
            TintOpacity = 0.8F,
        };
    }

    private static bool IsLightTheme(SystemBackdropTheme theme)
    {
        return theme switch
        {
            SystemBackdropTheme.Light => true,
            SystemBackdropTheme.Dark => false,
            _ => GeneralHelpers.IsTaskbarLight(),
        };
    }
}
