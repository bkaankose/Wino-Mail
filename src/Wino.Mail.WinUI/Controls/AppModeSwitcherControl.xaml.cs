using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Controls.ModeSwitcher;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Hosts the library's <see cref="WinoModeSwitcher"/> in the navigation pane footer. It maps
/// the switcher items to <see cref="WinoApplicationMode"/>, hides the modes no account
/// provides, mirrors the persisted application mode as the selection, and navigates when
/// the user picks a mode.
/// </summary>
public sealed partial class AppModeSwitcherControl : UserControl
{
    private readonly IStatePersistanceService _statePersistenceService;
    private readonly INavigationService _navigationService;
    private readonly IAppModeReadinessService _readinessService;
    private readonly WinoModeSwitcherItem[] _modeItems;

    private AppModeVisibility? _visibility;
    private int _visibilityRequestId;

    [GeneratedDependencyProperty(DefaultValue = Orientation.Horizontal)]
    public partial Orientation Orientation { get; set; }

    public AppModeSwitcherControl()
    {
        _statePersistenceService = WinoApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
        _navigationService = WinoApplication.Current.Services.GetRequiredService<INavigationService>();
        _readinessService = WinoApplication.Current.Services.GetRequiredService<IAppModeReadinessService>();

        InitializeComponent();

        MailModeItem.Label = Translator.KeyboardShortcuts_ModeMail;
        CalendarModeItem.Label = Translator.KeyboardShortcuts_ModeCalendar;
        ContactsModeItem.Label = Translator.ContactsPage_Title;
        ToDoModeItem.Label = Translator.ToDoPage_Title;
        SettingsModeItem.Label = Translator.MenuSettings;

        // Same order as AppModeVisibility.SwitchableModes.
        _modeItems = [MailModeItem, CalendarModeItem, ContactsModeItem, ToDoModeItem];
    }

    partial void OnOrientationPropertyChanged(DependencyPropertyChangedEventArgs e) => Switcher.Orientation = Orientation;

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

    /// <summary>
    /// Nothing shows until the first visibility result so a reduced set never flashes as the
    /// five-mode switcher first.
    /// </summary>
    private void ApplyLayout()
    {
        if (_visibility == null)
        {
            Switcher.Visibility = Visibility.Collapsed;
            return;
        }

        for (var index = 0; index < _modeItems.Length; index++)
        {
            _modeItems[index].IsVisible = _visibility.IsVisible(AppModeVisibility.SwitchableModes[index]);
        }

        Switcher.Visibility = Visibility.Visible;
        UpdateSelection(_statePersistenceService.ApplicationMode);
    }

    private void UpdateSelection(WinoApplicationMode mode)
    {
        var item = GetItem(mode);
        Switcher.SelectedItem = item != null && item.IsVisible ? item : null;
    }

    private void SwitcherItemInvoked(object sender, WinoModeSwitcherItemInvokedEventArgs e)
    {
        var mode = GetMode(e.Item);

        if (mode == _statePersistenceService.ApplicationMode)
            return;

        _navigationService.ChangeApplicationMode(mode);
    }

    private WinoApplicationMode GetMode(WinoModeSwitcherItem item)
    {
        if (ReferenceEquals(item, SettingsModeItem))
            return WinoApplicationMode.Settings;

        var index = Array.IndexOf(_modeItems, item);
        return index >= 0 ? AppModeVisibility.SwitchableModes[index] : WinoApplicationMode.Mail;
    }

    private WinoModeSwitcherItem? GetItem(WinoApplicationMode mode)
    {
        if (mode == WinoApplicationMode.Settings)
            return SettingsModeItem;

        var index = Array.IndexOf(AppModeVisibility.SwitchableModes, mode);
        return index >= 0 ? _modeItems[index] : null;
    }
}
