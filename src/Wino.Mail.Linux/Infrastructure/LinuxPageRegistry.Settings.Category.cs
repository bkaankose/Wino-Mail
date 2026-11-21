using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsCategoryPages()
    {
        Register<FolderCustomizationPage>(WinoPage.FolderCustomizationPage, LinuxPageHost.Settings);
        Register<MailCategoryManagementPage>(WinoPage.MailCategoryManagementPage, LinuxPageHost.Settings);
    }
}
