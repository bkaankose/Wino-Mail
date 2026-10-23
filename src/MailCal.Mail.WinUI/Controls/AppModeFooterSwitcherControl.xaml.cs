using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain;
using WinRT;

namespace MailCal.Mail.WinUI.Controls;

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
        [DynamicWindowsRuntimeCast(typeof(Orientation))]
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public AppModeFooterSwitcherControl()
    {
        _statePersistenceService = MailCalApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
        _navigationService = MailCalApplication.Current.Services.GetRequiredService<INavigationService>();

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
        UpdateModeIcons();

        if (_isUpdatingSelection)
            return;

        var selectedMode = SelectedIndex switch
        {
            1 => MailCalApplicationMode.Calendar,
            2 => MailCalApplicationMode.Contacts,
            3 => MailCalApplicationMode.Tasks,
            4 => MailCalApplicationMode.Settings,
            _ => MailCalApplicationMode.Mail
        };

        if (selectedMode == _statePersistenceService.ApplicationMode)
            return;

        _navigationService.ChangeApplicationMode(selectedMode);
    }

    private void UpdateSelection(MailCalApplicationMode mode)
    {
        _isUpdatingSelection = true;
        SelectedIndex = mode switch
        {
            MailCalApplicationMode.Calendar => 1,
            MailCalApplicationMode.Contacts => 2,
            MailCalApplicationMode.Tasks => 3,
            MailCalApplicationMode.Settings => 4,
            _ => 0
        };
        _isUpdatingSelection = false;
        UpdateModeIcons();
    }

    // The selected mode shows its Filled glyph; the others stay Regular.
    private void UpdateModeIcons()
    {
        MailModeIcon.Icon = SelectedIndex == 0 ? MailCalIconGlyph.MailFilled : MailCalIconGlyph.Mail;
        CalendarModeIcon.Icon = SelectedIndex == 1 ? MailCalIconGlyph.CalendarFilled : MailCalIconGlyph.Calendar;
        ContactsModeIcon.Icon = SelectedIndex == 2 ? MailCalIconGlyph.PeopleFilled : MailCalIconGlyph.People;
        ToDoModeIcon.Icon = SelectedIndex == 3 ? MailCalIconGlyph.CheckmarkCircleFilled : MailCalIconGlyph.CheckmarkCircle;
        SettingsModeIcon.Icon = SelectedIndex == 4 ? MailCalIconGlyph.SettingsFilled : MailCalIconGlyph.Settings;
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
