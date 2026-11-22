using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsTemplatesPages()
    {
        Register<EmailTemplatesPage>(WinoPage.EmailTemplatesPage, LinuxPageHost.Settings);
        Register<CreateEmailTemplatePage>(WinoPage.CreateEmailTemplatePage, LinuxPageHost.Settings);
    }
}
