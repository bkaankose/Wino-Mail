using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Services.WidgetsAndTiles;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.WinUI.ViewModels;

/// <summary>
/// Settings › Companion › Widgets / Live Tiles. Each section is enabled only on the Windows version that has its
/// surface: widgets on Windows 11, Live Tiles on Windows 10.
/// </summary>
public sealed partial class WidgetsLiveTilesSettingsPageViewModel(WindowsShellPreferences shellPreferences) : CoreBaseViewModel
{
    public bool IsWidgetsSupported => WidgetsAndTilesService.AreWidgetsSupported;
    public bool IsWidgetsUnsupported => !WidgetsAndTilesService.AreWidgetsSupported;

    public bool IsWinoWidgetsEnabled
    {
        get => shellPreferences.IsWinoWidgetsEnabled;
        set
        {
            if (shellPreferences.IsWinoWidgetsEnabled == value) return;
            shellPreferences.IsWinoWidgetsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsLiveTilesSupported => WidgetsAndTilesService.AreLiveTilesSupported;
    public bool IsLiveTilesUnsupported => !WidgetsAndTilesService.AreLiveTilesSupported;
    public bool CanChooseLiveTileModes => WidgetsAndTilesService.AreLiveTilesSupported && IsLiveTilesEnabled;

    public bool IsLiveTilesEnabled
    {
        get => shellPreferences.IsLiveTilesEnabled;
        set
        {
            if (shellPreferences.IsLiveTilesEnabled == value) return;
            shellPreferences.IsLiveTilesEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanChooseLiveTileModes));
        }
    }

    public bool ShowMailOnLiveTiles
    {
        get => shellPreferences.ShowMailOnLiveTiles;
        set
        {
            if (shellPreferences.ShowMailOnLiveTiles == value) return;
            shellPreferences.ShowMailOnLiveTiles = value;
            OnPropertyChanged();
        }
    }

    public bool ShowCalendarOnLiveTiles
    {
        get => shellPreferences.ShowCalendarOnLiveTiles;
        set
        {
            if (shellPreferences.ShowCalendarOnLiveTiles == value) return;
            shellPreferences.ShowCalendarOnLiveTiles = value;
            OnPropertyChanged();
        }
    }

    public bool ShowTasksOnLiveTiles
    {
        get => shellPreferences.ShowTasksOnLiveTiles;
        set
        {
            if (shellPreferences.ShowTasksOnLiveTiles == value) return;
            shellPreferences.ShowTasksOnLiveTiles = value;
            OnPropertyChanged();
        }
    }

    public bool ShowDetails
    {
        get => shellPreferences.ShowDetailsInWidgetsAndTiles;
        set
        {
            if (shellPreferences.ShowDetailsInWidgetsAndTiles == value) return;
            shellPreferences.ShowDetailsInWidgetsAndTiles = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenNotificationSettings()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.NotificationSettings_Title, WinoPage.NotificationSettingsPage));
}
