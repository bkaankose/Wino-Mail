using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Email templates (Settings › Mail › Email templates). Owned by the email template settings work.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterTemplateSettingsPages()
    {
        Register<EmailTemplatesPageViewController>(WinoPage.EmailTemplatesPage, MacPageHost.SettingsWindow);
        Register<CreateEmailTemplatePageViewController>(WinoPage.CreateEmailTemplatePage, MacPageHost.SettingsWindow);
    }
}
