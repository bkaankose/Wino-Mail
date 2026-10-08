using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Backup and restore. Owned by the Wino Account dialogs work (WS7).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterBackupSettingsPages()
    {
        Register<BackupRestorePageViewController>(WinoPage.BackupRestorePage, MacPageHost.SettingsWindow);
    }
}
