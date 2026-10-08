using AppKit;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Reports whether the app's effective appearance is dark; the reader uses it for its initial theme.</summary>
internal sealed class MacUnderlyingThemeService : IUnderlyingThemeService
{
    public bool IsUnderlyingThemeDark()
    {
        if (NSThread.IsMain) return IsDark();
        bool dark = false;
        NSApplication.SharedApplication.InvokeOnMainThread(() => dark = IsDark());
        return dark;
    }

    private static bool IsDark()
    {
        var appearance = NSApplication.SharedApplication.EffectiveAppearance;
        var match = appearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]);
        return match == NSAppearance.NameDarkAqua.ToString();
    }
}
