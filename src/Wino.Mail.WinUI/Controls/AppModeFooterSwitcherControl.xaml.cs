using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppModeSwitcher;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Binds the pane footer switcher to the shell. The control underneath knows nothing about
/// modes; this is where an index becomes a <see cref="WinoApplicationMode"/> and back.
/// </summary>
public sealed partial class AppModeFooterSwitcherControl : UserControl
{
    /// <summary>
    /// The modes the strip offers, in the order they appear. Settings is absent on purpose:
    /// it is reached from the switcher's own settings button, not from a segment.
    /// </summary>
    private static readonly WinoApplicationMode[] Modes =
    [
        WinoApplicationMode.Mail,
        WinoApplicationMode.Calendar,
        WinoApplicationMode.Contacts,
        WinoApplicationMode.Tasks
    ];

    private readonly IStatePersistanceService _statePersistenceService;
    private readonly INavigationService _navigationService;

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
        ((AppModeFooterSwitcherControl)dependencyObject).Switcher.Orientation =
            ((AppModeFooterSwitcherControl)dependencyObject).Orientation;
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged += StatePropertyChanged;

        Switcher.Orientation = Orientation;
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

    private void ModeInvoked(object? sender, WinoAppModeInvokedEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Modes.Length)
            return;

        var selectedMode = Modes[e.Index];

        if (selectedMode == _statePersistenceService.ApplicationMode)
            return;

        _navigationService.ChangeApplicationMode(selectedMode);
    }

    private void SettingsInvoked(object? sender, EventArgs e)
    {
        if (_statePersistenceService.ApplicationMode == WinoApplicationMode.Settings)
            return;

        _navigationService.ChangeApplicationMode(WinoApplicationMode.Settings);
    }

    /// <summary>
    /// Settings is not one of the modes, so it lights the settings button and leaves the
    /// strip with nothing selected rather than borrowing a segment.
    /// </summary>
    private void UpdateSelection(WinoApplicationMode mode)
    {
        Switcher.SelectedIndex = Array.IndexOf(Modes, mode);
        Switcher.IsSettingsSelected = mode == WinoApplicationMode.Settings;
    }
}
