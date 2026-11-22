using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsBackupPages() => Register<BackupRestorePage>(WinoPage.BackupRestorePage, LinuxPageHost.Settings);
}
