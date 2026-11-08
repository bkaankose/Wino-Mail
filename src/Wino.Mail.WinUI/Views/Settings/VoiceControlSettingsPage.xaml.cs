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
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.Controls.HotKeyInput;
using Wino.Mail.WinUI;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class VoiceControlSettingsPage : VoiceControlSettingsPageAbstract
{
    private readonly ShortcutRow _voiceShortcut;
    private readonly ShortcutRow _textShortcut;

    public VoiceControlSettingsPage()
    {
        InitializeComponent();
        var preferences = ViewModel.PreferencesService;
        _voiceShortcut = new ShortcutRow(this, VoiceBubbleInputMode.Voice, HotKeyEnabledToggle, HotKeyInput, VirtualKey.P,
            () => preferences.IsVoiceHotKeyEnabled, value => preferences.IsVoiceHotKeyEnabled = value,
            () => new HotKeyGesture(preferences.VoiceHotKeyKey, preferences.VoiceHotKeyModifiers),
            gesture => { preferences.VoiceHotKeyKey = gesture.Key; preferences.VoiceHotKeyModifiers = gesture.Modifiers; });
        _textShortcut = new ShortcutRow(this, VoiceBubbleInputMode.Text, TextHotKeyEnabledToggle, TextHotKeyInput, VirtualKey.T,
            () => preferences.IsVoiceTextHotKeyEnabled, value => preferences.IsVoiceTextHotKeyEnabled = value,
            () => new HotKeyGesture(preferences.VoiceTextHotKeyKey, preferences.VoiceTextHotKeyModifiers),
            gesture => { preferences.VoiceTextHotKeyKey = gesture.Key; preferences.VoiceTextHotKeyModifiers = gesture.Modifiers; });
        _voiceShortcut.Initialize();
        _textShortcut.Initialize();
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

    private void HotKeyEnabled_Toggled(object sender, RoutedEventArgs e) => _voiceShortcut.Toggled(sender);

    private void HotKeyInput_CaptureStarted(object? sender, EventArgs e) => _voiceShortcut.CaptureStarted();

    private void HotKeyInput_CaptureCanceled(object? sender, EventArgs e) => _voiceShortcut.CaptureCanceled();

    private void HotKeyInput_HotKeyCommitted(object? sender, HotKeyCommittedEventArgs e) => _voiceShortcut.Committed(e);

    private void TextHotKeyEnabled_Toggled(object sender, RoutedEventArgs e) => _textShortcut.Toggled(sender);

    private void TextHotKeyInput_CaptureStarted(object? sender, EventArgs e) => _textShortcut.CaptureStarted();

    private void TextHotKeyInput_CaptureCanceled(object? sender, EventArgs e) => _textShortcut.CaptureCanceled();

    private void TextHotKeyInput_HotKeyCommitted(object? sender, HotKeyCommittedEventArgs e) => _textShortcut.Committed(e);

    private void UpdateHotKeyAvailability()
    {
        var isEnabled = ViewModel.IsVoiceControlEnabled;
        VoiceHotKeyExpander.IsEnabled = isEnabled;
        HotKeyInput.IsEnabled = isEnabled && HotKeyEnabledToggle.IsOn;
        TextHotKeyExpander.IsEnabled = isEnabled;
        TextHotKeyInput.IsEnabled = isEnabled && TextHotKeyEnabledToggle.IsOn;
    }

    /// <summary>
    /// One shortcut's toggle and key recorder: the voice shortcut and the typing shortcut behave the same way and share
    /// the page's error bar.
    /// </summary>
    private sealed class ShortcutRow(
        VoiceControlSettingsPage page,
        VoiceBubbleInputMode mode,
        ToggleSwitch toggle,
        WinoHotKeyInput input,
        VirtualKey fallbackKey,
        Func<bool> getEnabled,
        Action<bool> setEnabled,
        Func<HotKeyGesture> getGesture,
        Action<HotKeyGesture> setGesture)
    {
        private bool _isUpdatingToggle;

        public void Initialize()
        {
            toggle.IsOn = getEnabled();
            Restore();
        }

        public void Toggled(object sender)
        {
            if (_isUpdatingToggle || sender is not ToggleSwitch { IsLoaded: true })
                return;

            page.HotKeyErrorInfoBar.IsOpen = false;
            if (!TryConfigure(toggle.IsOn, getGesture()))
            {
                page.HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
                page.HotKeyErrorInfoBar.IsOpen = true;
                _isUpdatingToggle = true;
                toggle.IsOn = getEnabled();
                _isUpdatingToggle = false;
                return;
            }

            setEnabled(toggle.IsOn);
            page.UpdateHotKeyAvailability();
        }

        public void CaptureStarted()
        {
            page.HotKeyErrorInfoBar.IsOpen = false;

            // A registered global hotkey never reaches the window as key input, so pressing the current shortcut while
            // listening would open the bubble instead of being captured.
            TryConfigure(false, getGesture());
        }

        public void CaptureCanceled()
        {
            Restore();
            Resume();
        }

        public void Committed(HotKeyCommittedEventArgs e)
        {
            page.HotKeyErrorInfoBar.IsOpen = false;

            var candidate = new HotKeyGesture(e.Key.ToString(), ToDomainModifiers(e.Modifiers)).Normalize();
            if (!candidate.IsValid)
            {
                ShowError(Translator.CompanionSettings_HotKey_Invalid);
                return;
            }

            if (!TryConfigure(getEnabled(), candidate))
            {
                ShowError(Translator.CompanionSettings_HotKey_Conflict);
                return;
            }

            setGesture(candidate);
            input.Key = e.Key;
            input.Modifiers = e.Modifiers;
        }

        private void ShowError(string message)
        {
            Restore();
            Resume();

            page.HotKeyErrorInfoBar.Message = message;
            page.HotKeyErrorInfoBar.IsOpen = true;
        }

        private void Resume()
        {
            if (TryConfigure(getEnabled(), getGesture()))
                return;

            page.HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
            page.HotKeyErrorInfoBar.IsOpen = true;
        }

        private bool TryConfigure(bool enabled, HotKeyGesture gesture) =>
            WinoApplication.Current is App app && app.TryConfigureVoiceHotKey(enabled, gesture, mode);

        private void Restore()
        {
            var gesture = getGesture();
            input.Key = Enum.TryParse(gesture.Key, true, out VirtualKey key) ? key : fallbackKey;
            input.Modifiers = ToVirtualModifiers(gesture.Modifiers);
        }
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
