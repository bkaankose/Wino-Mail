using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class AppModeFooterSwitcherControl : Segmented
{
    private const double VerticalItemExtent = 44;
    private readonly IStatePersistanceService _statePersistenceService;
    private readonly INavigationService _navigationService;
    private bool _isUpdatingSelection;

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(AppModeFooterSwitcherControl),
        new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public AppModeFooterSwitcherControl()
    {
        _statePersistenceService = WinoApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
        _navigationService = WinoApplication.Current.Services.GetRequiredService<INavigationService>();

        InitializeComponent();
    }

    private static void OnOrientationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        ((AppModeFooterSwitcherControl)dependencyObject).UpdateOrientationState();
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged += StatePropertyChanged;
        UpdateOrientationState();
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

        if (SelectedIndex == 3)
        {
            _navigationService.Navigate(WinoPage.SettingsPage);
            UpdateSelection(_statePersistenceService.ApplicationMode);
            return;
        }

        var selectedMode = SelectedIndex switch
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
        SelectedIndex = mode switch
        {
            WinoApplicationMode.Calendar => 1,
            WinoApplicationMode.Contacts => 2,
            _ => 0
        };
        _isUpdatingSelection = false;
    }

    private void UpdateOrientationState()
    {
        foreach (var item in Items)
        {
            if (item is not SegmentedItem segmentedItem)
                continue;

            if (Orientation == Orientation.Vertical)
            {
                segmentedItem.Width = VerticalItemExtent;
                segmentedItem.Height = VerticalItemExtent;
            }
            else
            {
                segmentedItem.ClearValue(WidthProperty);
                segmentedItem.ClearValue(HeightProperty);
            }
        }
    }
}
