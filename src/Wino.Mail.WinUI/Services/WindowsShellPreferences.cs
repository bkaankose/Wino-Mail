using System;
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

    /// <summary>Raised when a Live Tiles or widget preference changes, so their content is published again.</summary>
    public event EventHandler? WidgetsAndTilesPreferencesChanged;

    /// <summary>The taskbar icon stays the same when the app mode changes in-app.</summary>
    public bool IsAppModeTaskbarIconSwitchDisabled
    {
        get => configurationService.Get(AppModeTaskbarIconSwitchDisabledKey, false);
        set => configurationService.Set(AppModeTaskbarIconSwitchDisabledKey, value);
    }

    /// <summary>When off, widgets show an Enable widgets button and nothing updates them.</summary>
    public bool IsWinoWidgetsEnabled
    {
        get => configurationService.Get(nameof(IsWinoWidgetsEnabled), true);
        set => SetWidgetsAndTilesPreference(nameof(IsWinoWidgetsEnabled), value);
    }

    public bool IsLiveTilesEnabled
    {
        get => configurationService.Get(nameof(IsLiveTilesEnabled), true);
        set => SetWidgetsAndTilesPreference(nameof(IsLiveTilesEnabled), value);
    }

    public bool ShowMailOnLiveTiles
    {
        get => configurationService.Get(nameof(ShowMailOnLiveTiles), true);
        set => SetWidgetsAndTilesPreference(nameof(ShowMailOnLiveTiles), value);
    }

    public bool ShowCalendarOnLiveTiles
    {
        get => configurationService.Get(nameof(ShowCalendarOnLiveTiles), true);
        set => SetWidgetsAndTilesPreference(nameof(ShowCalendarOnLiveTiles), value);
    }

    public bool ShowTasksOnLiveTiles
    {
        get => configurationService.Get(nameof(ShowTasksOnLiveTiles), true);
        set => SetWidgetsAndTilesPreference(nameof(ShowTasksOnLiveTiles), value);
    }

    /// <summary>When off, widgets and Live Tiles show only counts and times.</summary>
    public bool ShowDetailsInWidgetsAndTiles
    {
        get => configurationService.Get(nameof(ShowDetailsInWidgetsAndTiles), true);
        set => SetWidgetsAndTilesPreference(nameof(ShowDetailsInWidgetsAndTiles), value);
    }

    private void SetWidgetsAndTilesPreference(string key, bool value)
    {
        if (configurationService.Get(key, true) == value)
            return;

        configurationService.Set(key, value);
        WidgetsAndTilesPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }
}
