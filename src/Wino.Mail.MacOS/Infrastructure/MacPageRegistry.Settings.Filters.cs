using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account mail filters: the rule list and the rule editor.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterFilterSettingsPages()
    {
        // Parameters as on Windows: the account Guid, then a MailFilterEditorNavigationParameter.
        Register<MailFiltersPageViewController>(WinoPage.MailFiltersPage, MacPageHost.SettingsWindow);
        Register<MailFilterEditorPageViewController>(WinoPage.MailFilterEditorPage, MacPageHost.SettingsWindow);
    }
}
