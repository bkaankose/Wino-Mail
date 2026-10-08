using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Companion settings (the menu bar companion). Owned by the Companion work.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterCompanionSettingsPages()
    {
        Register<CompanionSettingsPageViewController>(WinoPage.CompanionSettingsPage, MacPageHost.SettingsWindow);
    }
}
