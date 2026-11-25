using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Services.WidgetsAndTiles;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.WinUI.ViewModels;

/// <summary>
/// Settings › Companion › Widgets / Live Tiles. Widgets need Windows 11. Live Tiles are offered on every version,
/// because Start menu replacements on Windows 11 show them too.
/// </summary>
public sealed partial class WidgetsLiveTilesSettingsPageViewModel(IMessenger messenger, WindowsShellPreferences shellPreferences) : CoreBaseViewModel(messenger)
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

    /// <summary>Windows 11's own Start menu ignores tiles; a replacement Start menu may still show them.</summary>
    public bool IsLiveTileStartMenuMissing => !WidgetsAndTilesService.HasLiveTileStartMenu;
    public bool CanChooseLiveTileModes => IsLiveTilesEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PinToStartCommand))]
    public partial bool CanPinToStart { get; set; }

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

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = RefreshPinToStartAsync();
    }

    private async Task RefreshPinToStartAsync()
    {
        var canPin = await MailTileStartPin.CanRequestPinAsync();
        await ExecuteUIThread(() => CanPinToStart = canPin);
    }

    [RelayCommand(CanExecute = nameof(CanPinToStart))]
    private async Task PinToStartAsync()
    {
        if (await MailTileStartPin.RequestPinAsync())
            CanPinToStart = false;
    }

    [RelayCommand]
    private void OpenNotificationSettings()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.NotificationSettings_Title, WinoPage.NotificationSettingsPage));
}
