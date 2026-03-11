using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class AppModeFooterSwitcherControl : UserControl
{
    private readonly IStatePersistanceService _statePersistenceService;
    private readonly INavigationService _navigationService;
    private bool _isUpdatingSelection;

    public AppModeFooterSwitcherControl()
    {
        _statePersistenceService = WinoApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
        _navigationService = WinoApplication.Current.Services.GetRequiredService<INavigationService>();

        InitializeComponent();
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged += StatePropertyChanged;
        UpdateSelection(_statePersistenceService.ApplicationMode);
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged -= StatePropertyChanged;
    }
    private void StatePropertyChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IStatePersistanceService.ApplicationMode))
            return;

        DispatcherQueue.TryEnqueue(() => UpdateSelection(_statePersistenceService.ApplicationMode));
    }

    private void ModeSegmentedControlSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection)
            return;

        if (ModeSegmentedControl.SelectedIndex == 3)
        {
            _navigationService.Navigate(WinoPage.SettingsPage);
            UpdateSelection(_statePersistenceService.ApplicationMode);
            return;
        }

        var selectedMode = ModeSegmentedControl.SelectedIndex switch
        {
            1 => WinoApplicationMode.Calendar,
            2 => WinoApplicationMode.Contacts,
            _ => WinoApplicationMode.Mail
        };

        if (selectedMode == _statePersistenceService.ApplicationMode)
            return;

        _navigationService.ChangeApplicationMode(selectedMode);
    }

    private void UpdateSelection(WinoApplicationMode mode)
    {
        _isUpdatingSelection = true;
        ModeSegmentedControl.SelectedIndex = mode switch
        {
            WinoApplicationMode.Calendar => 1,
            WinoApplicationMode.Contacts => 2,
            _ => 0
        };
        _isUpdatingSelection = false;
    }
}
