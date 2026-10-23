using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models;
using MailCal.Mail.Controls.HotKeyInput;
using MailCal.Mail.WinUI;
using MailCal.Views.Abstract;

namespace MailCal.Views.Settings;

public sealed partial class CompanionSettingsPage : CompanionSettingsPageAbstract
{
    private bool _isUpdatingHotKeyToggle;

    public CompanionSettingsPage()
    {
        InitializeComponent();
        HotKeyEnabledToggle.IsOn = ViewModel.PreferencesService.IsCompanionHotKeyEnabled;
        RestoreHotKeyInput();
        UpdateHotKeyAvailability();

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
            UpdateHotKeyAvailability(toggle.IsOn);
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
        MailCalApplication.Current is App app && app.TryConfigureCompanionHotKey(enabled, gesture);

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
