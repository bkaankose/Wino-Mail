using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Voice Control settings and What Wino remembers.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterVoiceSettingsPages()
    {
        Register<VoiceControlSettingsPageViewController>(WinoPage.VoiceControlSettingsPage, MacPageHost.SettingsWindow);
        Register<VoiceBrainPageViewController>(WinoPage.VoiceBrainPage, MacPageHost.SettingsWindow);
    }
}
