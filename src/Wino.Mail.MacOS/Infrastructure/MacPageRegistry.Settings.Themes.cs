using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Application theme gallery and custom theme editor (Settings › Personalization › Application themes).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterThemeSettingsPages()
    {
        Register<ApplicationThemeGalleryPageViewController>(WinoPage.ApplicationThemeGalleryPage, MacPageHost.SettingsWindow);
        Register<ApplicationThemeEditorPageViewController>(WinoPage.ApplicationThemeEditorPage, MacPageHost.SettingsWindow);
    }
}
