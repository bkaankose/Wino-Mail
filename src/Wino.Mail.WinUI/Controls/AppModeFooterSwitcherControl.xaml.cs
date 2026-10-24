using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using WinRT;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class AppModeFooterSwitcherControl : UserControl
{
    private const double VerticalItemExtent = 44;
    private const double DockedSettingsExtent = 44;
    private const double DockedSettingsGap = 12;

    private readonly IStatePersistanceService _statePersistenceService;
    private readonly INavigationService _navigationService;
    private readonly IAppModeReadinessService _readinessService;
    private readonly SegmentedItem[] _modeItems;
    private readonly WinoFontIcon[] _modeIcons;

    private AppModeVisibility _visibility;
    private Grid _itemsPanel;
    private bool _isUpdatingSelection;
    private int _visibilityRequestId;

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
        _statePersistenceService = WinoApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
        _navigationService = WinoApplication.Current.Services.GetRequiredService<INavigationService>();
        _readinessService = WinoApplication.Current.Services.GetRequiredService<IAppModeReadinessService>();

        InitializeComponent();

        // Same order as AppModeVisibility.SwitchableModes.
        _modeItems = [MailModeItem, CalendarModeItem, ContactsModeItem, ToDoModeItem];
        _modeIcons = [MailModeIcon, CalendarModeIcon, ContactsModeIcon, ToDoModeIcon];
    }

    private static void OnOrientationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        ((AppModeFooterSwitcherControl)dependencyObject).ArrangeItems();
    }

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged += StatePropertyChanged;
        _readinessService.ReadinessInvalidated += ReadinessInvalidated;

        ApplyLayout();
        _ = RefreshVisibilityAsync();
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        _statePersistenceService.StatePropertyChanged -= StatePropertyChanged;
        _readinessService.ReadinessInvalidated -= ReadinessInvalidated;
    }

    // The card starts collapsed, so the Segmented template (and its panel) applies only after
    // the first visibility result. The panel announces itself once it exists.
    private void ItemsPanelLoaded(object sender, RoutedEventArgs e)
    {
        _itemsPanel = sender as Grid;
        ArrangeItems();
    }

    private void StatePropertyChanged(object sender, string propertyName)
    {
        if (propertyName != nameof(IStatePersistanceService.ApplicationMode))
            return;

        DispatcherQueue.TryEnqueue(() => UpdateSelection(_statePersistenceService.ApplicationMode));
    }

    private void ReadinessInvalidated(object sender, EventArgs e) => _ = RefreshVisibilityAsync();

    private async Task RefreshVisibilityAsync()
    {
        var requestId = Interlocked.Increment(ref _visibilityRequestId);
        var visibility = await _readinessService.GetVisibilityAsync();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (requestId != _visibilityRequestId || visibility.Equals(_visibility))
                return;

            _visibility = visibility;
            ApplyLayout();
        });
    }

    #region Layout

    /// <summary>
    /// Nothing shows until the first visibility result so a reduced set never flashes as the
    /// five-segment switcher first.
    /// </summary>
    private void ApplyLayout()
    {
        if (_visibility == null)
        {
            SwitcherCard.Visibility = Visibility.Collapsed;
            return;
        }

        SwitcherCard.Visibility = Visibility.Visible;

        var desiredItems = _visibility.VisibleModes
            .Select(mode => _modeItems[Array.IndexOf(AppModeVisibility.SwitchableModes, mode)])
            .Append(SettingsModeItem)
            .ToList();

        if (!ModeSegmented.Items.SequenceEqual(desiredItems))
        {
            _isUpdatingSelection = true;

            try
            {
                ModeSegmented.Items.Clear();

                foreach (var item in desiredItems)
                    ModeSegmented.Items.Add(item);
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        ArrangeItems();
        UpdateSelection(_statePersistenceService.ApplicationMode);
    }

    /// <summary>
    /// Every mode present: equal segments, as the original control. Fewer modes, down to a
    /// single one: Settings docks to the far end at a fixed size behind a divider and the modes
    /// share the rest, so Settings always has a way back. The compact pane stacks the same
    /// sequence vertically.
    /// </summary>
    private void ArrangeItems()
    {
        var isVertical = Orientation == Orientation.Vertical;

        VisualStateManager.GoToState(this, isVertical ? "VerticalLayout" : "HorizontalLayout", false);

        if ((_itemsPanel ?? ModeSegmented.ItemsPanelRoot) is not Grid panel)
            return;

        var dockSettings = ModeSegmented.Items.Count <= AppModeVisibility.SwitchableModes.Length;

        panel.ColumnDefinitions.Clear();
        panel.RowDefinitions.Clear();

        for (var index = 0; index < ModeSegmented.Items.Count; index++)
        {
            if (ModeSegmented.Items[index] is not SegmentedItem item)
                continue;

            var isDockedSettings = dockSettings && ReferenceEquals(item, SettingsModeItem);

            if (isVertical)
            {
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(item, index);
                Grid.SetColumn(item, 0);
                item.Width = VerticalItemExtent;
                item.Height = VerticalItemExtent;
                item.Margin = isDockedSettings ? new Thickness(0, DockedSettingsGap, 0, 0) : new Thickness(0);
            }
            else
            {
                panel.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = isDockedSettings ? GridLength.Auto : new GridLength(1, GridUnitType.Star)
                });
                Grid.SetRow(item, 0);
                Grid.SetColumn(item, index);
                item.ClearValue(HeightProperty);
                item.Margin = isDockedSettings ? new Thickness(DockedSettingsGap, 0, 0, 0) : new Thickness(0);

                if (isDockedSettings)
                {
                    item.Width = DockedSettingsExtent;
                }
                else
                {
                    item.ClearValue(WidthProperty);
                }
            }
        }

        SettingsDivider.Visibility = dockSettings ? Visibility.Visible : Visibility.Collapsed;

        // The divider sits in the middle of the gap between the last mode and Settings.
        var dividerOffset = DockedSettingsExtent + (DockedSettingsGap / 2);

        if (isVertical)
        {
            SettingsDivider.ClearValue(WidthProperty);
            SettingsDivider.Height = 1;
            SettingsDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
            SettingsDivider.VerticalAlignment = VerticalAlignment.Bottom;
            SettingsDivider.Margin = new Thickness(4, 0, 4, dividerOffset);
        }
        else
        {
            SettingsDivider.ClearValue(HeightProperty);
            SettingsDivider.Width = 1;
            SettingsDivider.HorizontalAlignment = HorizontalAlignment.Right;
            SettingsDivider.VerticalAlignment = VerticalAlignment.Stretch;
            SettingsDivider.Margin = new Thickness(0, 4, dividerOffset, 4);
        }
    }

    #endregion

    #region Selection

    private void ModeSegmentedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeIcons();

        if (_isUpdatingSelection || ModeSegmented.SelectedItem is not SegmentedItem selectedItem)
            return;

        var selectedMode = GetMode(selectedItem);

        if (selectedMode == _statePersistenceService.ApplicationMode)
            return;

        _navigationService.ChangeApplicationMode(selectedMode);
    }

    private void UpdateSelection(WinoApplicationMode mode)
    {
        _isUpdatingSelection = true;

        try
        {
            var item = GetItem(mode);
            ModeSegmented.SelectedItem = item != null && ModeSegmented.Items.Contains(item) ? item : null;
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        UpdateModeIcons();
    }

    // The selected mode shows its Filled glyph; the others stay Regular.
    private void UpdateModeIcons()
    {
        var selectedItem = ModeSegmented.SelectedItem as SegmentedItem;

        MailModeIcon.Icon = ReferenceEquals(selectedItem, MailModeItem) ? WinoIconGlyph.MailFilled : WinoIconGlyph.Mail;
        CalendarModeIcon.Icon = ReferenceEquals(selectedItem, CalendarModeItem) ? WinoIconGlyph.CalendarFilled : WinoIconGlyph.Calendar;
        ContactsModeIcon.Icon = ReferenceEquals(selectedItem, ContactsModeItem) ? WinoIconGlyph.PeopleFilled : WinoIconGlyph.People;
        ToDoModeIcon.Icon = ReferenceEquals(selectedItem, ToDoModeItem) ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.CheckmarkCircle;
        SettingsModeIcon.Icon = ReferenceEquals(selectedItem, SettingsModeItem) ? WinoIconGlyph.SettingsFilled : WinoIconGlyph.Settings;
    }

    private WinoApplicationMode GetMode(SegmentedItem item)
    {
        if (ReferenceEquals(item, SettingsModeItem))
            return WinoApplicationMode.Settings;

        var index = Array.IndexOf(_modeItems, item);
        return index >= 0 ? AppModeVisibility.SwitchableModes[index] : WinoApplicationMode.Mail;
    }

    private SegmentedItem GetItem(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings)
            return SettingsModeItem;

        var index = Array.IndexOf(AppModeVisibility.SwitchableModes, mode);
        return index >= 0 ? _modeItems[index] : null;
    }

    #endregion
}
