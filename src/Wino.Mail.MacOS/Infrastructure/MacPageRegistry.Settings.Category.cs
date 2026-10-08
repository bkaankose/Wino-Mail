using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Mail category management. Owned by the core dialogs work (WS4).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterCategorySettingsPages()
    {
        Register<MailCategoryManagementPageViewController>(WinoPage.MailCategoryManagementPage, MacPageHost.SettingsWindow);
    }
}
