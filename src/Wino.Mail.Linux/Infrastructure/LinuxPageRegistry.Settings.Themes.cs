using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsThemesPages()
    {
        Register<PersonalizationPage>(WinoPage.PersonalizationPage, LinuxPageHost.Settings);
        Register<ApplicationThemeGalleryPage>(WinoPage.ApplicationThemeGalleryPage, LinuxPageHost.Settings);
        Register<ApplicationThemeEditorPage>(WinoPage.ApplicationThemeEditorPage, LinuxPageHost.Settings);
    }
}
