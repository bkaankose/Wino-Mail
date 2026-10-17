using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Windows-only shell preferences. They stay out of <see cref="IPreferencesService"/>, so the shared layer, other
/// platforms and Wino Account settings sync never see them. The key matches the former shared preference, so the
/// stored value carries over.
/// </summary>
public sealed class WindowsShellPreferences(IConfigurationService configurationService)
{
    private const string AppModeTaskbarIconSwitchDisabledKey = "IsAppModeTaskbarIconSwitchDisabled";

    /// <summary>The taskbar icon stays the same when the app mode changes in-app.</summary>
    public bool IsAppModeTaskbarIconSwitchDisabled
    {
        get => configurationService.Get(AppModeTaskbarIconSwitchDisabledKey, false);
        set => configurationService.Set(AppModeTaskbarIconSwitchDisabledKey, value);
    }
}
