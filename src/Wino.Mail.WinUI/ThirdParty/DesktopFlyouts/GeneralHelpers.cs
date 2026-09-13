// Copyright (c) 0x5BFA. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Win32;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

internal static class GeneralHelpers
{
    internal static bool IsTaskbarLight()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var value = key?.GetValue("SystemUsesLightTheme");
        return value is int v && v != 0;
    }

    internal static bool IsTaskbarColorPrevalenceEnabled()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var value = key?.GetValue("ColorPrevalence");
        return value is int v && v != 0;
    }


}
