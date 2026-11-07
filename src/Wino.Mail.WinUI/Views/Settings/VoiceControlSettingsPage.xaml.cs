using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.HotKeyInput;
using Wino.Mail.WinUI;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class VoiceControlSettingsPage : VoiceControlSettingsPageAbstract
{
    private bool _isUpdatingHotKeyToggle;

    public VoiceControlSettingsPage()
    {
        InitializeComponent();
        HotKeyEnabledToggle.IsOn = ViewModel.PreferencesService.IsVoiceHotKeyEnabled;
        RestoreHotKeyInput();
        UpdateHotKeyAvailability();
        UpdatePositionSpots();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        base.OnNavigatedFrom(e);
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedPosition))
            UpdatePositionSpots();
        else if (e.PropertyName == nameof(ViewModel.IsVoiceControlEnabled))
            UpdateHotKeyAvailability();
    }

    private void VoiceControlEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsLoaded: true })
            UpdateHotKeyAvailability();
    }

    private void PositionSpot_Click(object sender, RoutedEventArgs e)
    {
        var position = sender switch
        {
            _ when ReferenceEquals(sender, TopLeftSpot) => VoiceBubblePosition.TopLeft,
            _ when ReferenceEquals(sender, TopRightSpot) => VoiceBubblePosition.TopRight,
            _ when ReferenceEquals(sender, CenterSpot) => VoiceBubblePosition.Center,
            _ when ReferenceEquals(sender, BottomLeftSpot) => VoiceBubblePosition.BottomLeft,
            _ => VoiceBubblePosition.BottomRight
        };

        ViewModel.SelectedPosition = position;
        UpdatePositionSpots();
    }

    private void UpdatePositionSpots()
    {
        var position = ViewModel.SelectedPosition;
        SetSpot(TopLeftSpot, position == VoiceBubblePosition.TopLeft);
        SetSpot(TopRightSpot, position == VoiceBubblePosition.TopRight);
        SetSpot(CenterSpot, position == VoiceBubblePosition.Center);
        SetSpot(BottomLeftSpot, position == VoiceBubblePosition.BottomLeft);
        SetSpot(BottomRightSpot, position == VoiceBubblePosition.BottomRight);
    }

    private static void SetSpot(ToggleButton spot, bool isSelected)
    {
        if (spot.IsChecked != isSelected)
            spot.IsChecked = isSelected;
    }

    private void HotKeyEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingHotKeyToggle || sender is not ToggleSwitch { IsLoaded: true } toggle)
            return;

        HotKeyErrorInfoBar.IsOpen = false;
        if (!TryConfigure(toggle.IsOn, GetStoredGesture()))
        {
            HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
            HotKeyErrorInfoBar.IsOpen = true;
            _isUpdatingHotKeyToggle = true;
            toggle.IsOn = ViewModel.PreferencesService.IsVoiceHotKeyEnabled;
            _isUpdatingHotKeyToggle = false;
            return;
        }

        ViewModel.PreferencesService.IsVoiceHotKeyEnabled = toggle.IsOn;
        UpdateHotKeyAvailability();
    }

    private void HotKeyInput_CaptureStarted(object? sender, EventArgs e)
    {
        HotKeyErrorInfoBar.IsOpen = false;

        // A registered global hotkey never reaches the window as key input, so pressing the
        // current shortcut while listening would open the voice bubble instead of being captured.
        TryConfigure(false, GetStoredGesture());
    }

    private void HotKeyInput_CaptureCanceled(object? sender, EventArgs e)
    {
        RestoreHotKeyInput();
        ResumeStoredHotKey();
    }

    private void HotKeyInput_HotKeyCommitted(object? sender, HotKeyCommittedEventArgs e)
    {
        HotKeyErrorInfoBar.IsOpen = false;

        var candidate = new HotKeyGesture(e.Key.ToString(), ToDomainModifiers(e.Modifiers)).Normalize();
        if (!candidate.IsValid)
        {
            ShowHotKeyError(Translator.CompanionSettings_HotKey_Invalid);
            return;
        }

        if (!TryConfigure(ViewModel.PreferencesService.IsVoiceHotKeyEnabled, candidate))
        {
            ShowHotKeyError(Translator.CompanionSettings_HotKey_Conflict);
            return;
        }

        ViewModel.PreferencesService.VoiceHotKeyKey = candidate.Key;
        ViewModel.PreferencesService.VoiceHotKeyModifiers = candidate.Modifiers;
        HotKeyInput.Key = e.Key;
        HotKeyInput.Modifiers = e.Modifiers;
    }

    private void ShowHotKeyError(string message)
    {
        RestoreHotKeyInput();
        ResumeStoredHotKey();

        HotKeyErrorInfoBar.Message = message;
        HotKeyErrorInfoBar.IsOpen = true;
    }

    private void ResumeStoredHotKey()
    {
        if (TryConfigure(ViewModel.PreferencesService.IsVoiceHotKeyEnabled, GetStoredGesture()))
            return;

        HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
        HotKeyErrorInfoBar.IsOpen = true;
    }

    private static bool TryConfigure(bool enabled, HotKeyGesture gesture) =>
        WinoApplication.Current is App app && app.TryConfigureVoiceHotKey(enabled, gesture);

    private HotKeyGesture GetStoredGesture() => new(
        ViewModel.PreferencesService.VoiceHotKeyKey,
        ViewModel.PreferencesService.VoiceHotKeyModifiers);

    private void RestoreHotKeyInput()
    {
        var gesture = GetStoredGesture();
        HotKeyInput.Key = Enum.TryParse(gesture.Key, true, out VirtualKey key) ? key : VirtualKey.P;
        HotKeyInput.Modifiers = ToVirtualModifiers(gesture.Modifiers);
    }

    private void UpdateHotKeyAvailability()
    {
        var isEnabled = ViewModel.IsVoiceControlEnabled;
        VoiceHotKeyExpander.IsEnabled = isEnabled;
        HotKeyInput.IsEnabled = isEnabled && HotKeyEnabledToggle.IsOn;
    }

    private static ModifierKeys ToDomainModifiers(VirtualKeyModifiers modifiers)
    {
        var result = ModifierKeys.None;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
            result |= ModifierKeys.Control;
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
            result |= ModifierKeys.Alt;
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
            result |= ModifierKeys.Shift;
        if (modifiers.HasFlag(VirtualKeyModifiers.Windows))
            result |= ModifierKeys.Windows;
        return result;
    }

    private static VirtualKeyModifiers ToVirtualModifiers(ModifierKeys modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= VirtualKeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= VirtualKeyModifiers.Menu;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= VirtualKeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= VirtualKeyModifiers.Windows;
        return result;
    }
}
