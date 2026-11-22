using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings.Intelligence;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsIntelligencePages()
    {
        Register<WinoIntelligencePage>(WinoPage.WinoIntelligencePage, LinuxPageHost.Settings);
        Register<MailClassificationPage>(WinoPage.MailClassificationPage, LinuxPageHost.Settings);
        Register<WinoIntelligenceManagementPage>(WinoPage.WinoIntelligenceManagementPage, LinuxPageHost.Settings);
        Register<IntelligenceCoveragePage>(WinoPage.IntelligenceCoveragePage, LinuxPageHost.Settings);
    }
}
