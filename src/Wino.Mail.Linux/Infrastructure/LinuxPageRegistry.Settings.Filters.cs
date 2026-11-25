using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsFiltersPages()
    {
        Register<MailFiltersPage>(WinoPage.MailFiltersPage, LinuxPageHost.Settings);
        Register<MailFilterEditorPage>(WinoPage.MailFilterEditorPage, LinuxPageHost.Settings);
    }
}
