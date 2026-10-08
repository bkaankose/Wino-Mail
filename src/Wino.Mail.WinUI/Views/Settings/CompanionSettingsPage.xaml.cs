using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.HotKeyInput;
using Wino.Mail.WinUI;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class CompanionSettingsPage : CompanionSettingsPageAbstract
{
    private bool _isUpdatingHotKeyToggle;

    public CompanionSettingsPage()
    {
        InitializeComponent();
        HotKeyEnabledToggle.IsOn = ViewModel.PreferencesService.IsCompanionHotKeyEnabled;
        UnreadBehaviorComboBox.SelectedItem = ViewModel.PreferencesService.CompanionUnreadMessageBehavior
            == CompanionUnreadMessageBehavior.Everything
                ? EverythingComboBoxItem
                : AfterAppSessionComboBoxItem;
        RestoreHotKeyInput();
        UpdateHotKeyAvailability();
        UpdatePersonalizationAvailability();

        if (HotKeyEnabledToggle.IsOn && !TryConfigure(true, GetStoredGesture()))
        {
            HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
            HotKeyErrorInfoBar.IsOpen = true;
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }

    private void CompanionEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsLoaded: true } toggle)
        {
            UpdateHotKeyAvailability(toggle.IsOn);
            UpdatePersonalizationAvailability(toggle.IsOn);
        }
    }

    private void UnreadBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { IsLoaded: true } comboBox)
            return;

        var behavior = ReferenceEquals(comboBox.SelectedItem, EverythingComboBoxItem)
            ? CompanionUnreadMessageBehavior.Everything
            : CompanionUnreadMessageBehavior.AfterAppSession;

        if (ViewModel.PreferencesService.CompanionUnreadMessageBehavior != behavior)
            ViewModel.PreferencesService.CompanionUnreadMessageBehavior = behavior;
    }

    private void HotKeyEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingHotKeyToggle || sender is not ToggleSwitch { IsLoaded: true } toggle)
            return;

        HotKeyErrorInfoBar.IsOpen = false;
        var gesture = GetStoredGesture();
        if (!TryConfigure(toggle.IsOn, gesture))
        {
            HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
            HotKeyErrorInfoBar.IsOpen = true;
            _isUpdatingHotKeyToggle = true;
            toggle.IsOn = ViewModel.PreferencesService.IsCompanionHotKeyEnabled;
            _isUpdatingHotKeyToggle = false;
            return;
        }

        ViewModel.PreferencesService.IsCompanionHotKeyEnabled = toggle.IsOn;
        UpdateHotKeyAvailability();
    }

    private void HotKeyInput_CaptureStarted(object? sender, EventArgs e)
    {
        HotKeyErrorInfoBar.IsOpen = false;

        // A registered global hotkey never reaches the window as key input, so pressing the
        // current shortcut while listening would open the companion instead of being captured.
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

        if (!TryConfigure(ViewModel.PreferencesService.IsCompanionHotKeyEnabled, candidate))
        {
            ShowHotKeyError(Translator.CompanionSettings_HotKey_Conflict);
            return;
        }

        ViewModel.PreferencesService.CompanionHotKeyKey = candidate.Key;
        ViewModel.PreferencesService.CompanionHotKeyModifiers = candidate.Modifiers;
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
        if (TryConfigure(ViewModel.PreferencesService.IsCompanionHotKeyEnabled, GetStoredGesture()))
            return;

        HotKeyErrorInfoBar.Message = Translator.CompanionSettings_HotKey_Conflict;
        HotKeyErrorInfoBar.IsOpen = true;
    }

    private bool TryConfigure(bool enabled, HotKeyGesture gesture) =>
        WinoApplication.Current is App app && app.TryConfigureCompanionHotKey(enabled, gesture);

    private HotKeyGesture GetStoredGesture() => new(
        ViewModel.PreferencesService.CompanionHotKeyKey,
        ViewModel.PreferencesService.CompanionHotKeyModifiers);

    private void RestoreHotKeyInput()
    {
        var gesture = GetStoredGesture();
        HotKeyInput.Key = Enum.TryParse(gesture.Key, true, out VirtualKey key) ? key : VirtualKey.Space;
        HotKeyInput.Modifiers = ToVirtualModifiers(gesture.Modifiers);
    }

    private void UpdateHotKeyAvailability(bool? isCompanionEnabled = null)
    {
        var isEnabled = isCompanionEnabled ?? ViewModel.PreferencesService.IsCompanionEnabled;
        HotKeyEnabledToggle.IsEnabled = isEnabled;
        HotKeyInput.IsEnabled = isEnabled && HotKeyEnabledToggle.IsOn;
    }

    private void UpdatePersonalizationAvailability(bool? isCompanionEnabled = null)
    {
        var isEnabled = isCompanionEnabled ?? ViewModel.PreferencesService.IsCompanionEnabled;
        CompanionContentExpander.IsEnabled = isEnabled;
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
