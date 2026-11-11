using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Wino Assistant settings, What Wino remembers, and the Wino Intelligence sub-pages.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterVoiceSettingsPages()
    {
        Register<VoiceControlSettingsPageViewController>(WinoPage.VoiceControlSettingsPage, MacPageHost.SettingsWindow);
        Register<VoiceBrainPageViewController>(WinoPage.VoiceBrainPage, MacPageHost.SettingsWindow);
        Register<MailClassificationPageViewController>(WinoPage.MailClassificationPage, MacPageHost.SettingsWindow);
    }
}
