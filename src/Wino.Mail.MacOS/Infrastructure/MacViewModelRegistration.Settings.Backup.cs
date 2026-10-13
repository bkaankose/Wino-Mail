using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Backup and restore views and ViewModels. Owned by the Wino Account dialogs work (WS7).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterBackupSettingsViews(IServiceCollection services)
    {
        // Same type and transient lifetime as the WinUI registration.
        services.AddTransient<BackupRestorePageViewController>();
    }
}
