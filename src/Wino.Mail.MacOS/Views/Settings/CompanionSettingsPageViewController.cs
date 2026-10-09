using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Platform.MacOS.Services;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Companion (Windows CompanionSettingsPage): turn the menu bar companion on or off, choose which
/// unread messages it shows, its global shortcut (a Carbon hotkey) and which sections appear.
/// The shortcut logic lives here, like the Windows CompanionSettingsPage code-behind.
/// </summary>
public sealed class CompanionSettingsPageViewController(CompanionSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    MacCompanionHotKeyController hotKey)
    : SettingsPageViewController<CompanionSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private WinoLabeledSwitch _hotKeySwitch = null!;
    private WinoSettingsExpander _hotKeyExpander = null!;
    private HotKeyRecorderView _recorder = null!;
    private NSButton _resetButton = null!;
    private WinoInfoBar _hotKeyError = null!;
    private bool _updatingHotKeySwitch;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        AddIntro(Translator.CompanionSettings_MacOS_About_Description);

        // The menu bar icon follows General › App close behavior; say so when it is not shown.
        if (vm.PreferencesService.AppCloseBehavior != AppCloseBehavior.RunInBackgroundWithTrayIcon)
            Add(InfoBar(WinoInfoBarSeverity.Informational, null, Translator.CompanionSettings_MacOS_StatusItemHint));

        var enabled = Bind.Switch(vm, nameof(vm.IsCompanionEnabled), s => s.IsCompanionEnabled, (s, v) => s.IsCompanionEnabled = v,
            Translator.CompanionSettings_Enable_Title);
        AddGroup(null, Card(Translator.CompanionSettings_Enable_Title, Translator.CompanionSettings_MacOS_Enable_Description, WinoIconGlyph.Alert, enabled));

        BuildShortcutGroup();

        var unreadBehavior = Bind.PopUp<CompanionSettingsPageViewModel, CompanionUnreadBehaviorOption>(vm,
            s => s.UnreadBehaviorOptions, option => option.DisplayText,
            nameof(vm.SelectedUnreadBehavior), s => s.SelectedUnreadBehavior, (s, v) => s.SelectedUnreadBehavior = v, width: 180);
        var unreadBehaviorCard = Bind.Enabled(
            Card(Translator.CompanionSettings_UnreadBehavior_Title, Translator.CompanionSettings_UnreadBehavior_Description, WinoIconGlyph.None, unreadBehavior),
            vm, nameof(vm.IsUnreadBehaviorEnabled), s => s.IsUnreadBehaviorEnabled);

        var content = Expander(Translator.CompanionSettings_Content_Title, Translator.CompanionSettings_Content_Description, WinoIconGlyph.Board, null,
            Card(Translator.CompanionSettings_ShowCalendar_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowCalendar), s => s.ShowCalendar, (s, v) => s.ShowCalendar = v, Translator.CompanionSettings_ShowCalendar_Title)),
            Card(Translator.CompanionSettings_ShowUnreadMail_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowUnreadMail), s => s.ShowUnreadMail, (s, v) => s.ShowUnreadMail = v, Translator.CompanionSettings_ShowUnreadMail_Title)),
            unreadBehaviorCard,
            Card(Translator.CompanionSettings_ShowTasks_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowTasks), s => s.ShowTasks, (s, v) => s.ShowTasks = v, Translator.CompanionSettings_ShowTasks_Title)),
            Card(Translator.CompanionSettings_ShowFavoriteContacts_Title, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.ShowFavoriteContacts), s => s.ShowFavoriteContacts, (s, v) => s.ShowFavoriteContacts = v, Translator.CompanionSettings_ShowFavoriteContacts_Title)));
        content.IsExpanded = true;
        AddGroup(null, Bind.Enabled(content, vm, nameof(vm.IsContentEnabled), s => s.IsContentEnabled));
    }

    #region Shortcut

    /// <summary>Windows CompanionHotKeyExpander: switch, recorder, reset, and the error bar under the card.</summary>
    private void BuildShortcutGroup()
    {
        var vm = ViewModel;
        _hotKeySwitch = new WinoLabeledSwitch { IsOn = vm.PreferencesService.IsCompanionHotKeyEnabled };
        WinoAccessibility.Label(_hotKeySwitch.Switch, Translator.CompanionSettings_HotKey_Title);
        Bind.OnActivated(_hotKeySwitch.Switch, HotKeySwitchToggled);

        _recorder = new HotKeyRecorderView();
        _recorder.CaptureStarted += RecorderCaptureStarted;
        _recorder.CaptureCanceled += RecorderCaptureCanceled;
        _recorder.Committed += RecorderCommitted;
        _recorder.Cleared += RecorderCleared;
        Bindings.Own(new ActionDisposable(() =>
        {
            _recorder.CaptureStarted -= RecorderCaptureStarted;
            _recorder.CaptureCanceled -= RecorderCaptureCanceled;
            _recorder.Committed -= RecorderCommitted;
            _recorder.Cleared -= RecorderCleared;
            // Leaving the page while listening must not leave the shortcut unregistered.
            if (_recorder.IsRecording) ResumeStoredHotKey();
        }));
        _recorder.SetGesture(StoredGesture);

        _resetButton = Bind.Button(Translator.Buttons_Reset, () => Commit(HotKeyGesture.Default));

        _hotKeyExpander = Expander(Translator.CompanionSettings_HotKey_Title, Translator.CompanionSettings_MacOS_HotKey_Description, WinoIconGlyph.Keyboard, _hotKeySwitch,
            Card(Translator.CompanionSettings_HotKey_KeyCombination, Translator.CompanionSettings_HotKey_Hint, WinoIconGlyph.None, _recorder),
            Card(Translator.CompanionSettings_HotKey_ResetToDefault, MacHotKeyKeys.Format(HotKeyGesture.Default), WinoIconGlyph.None, _resetButton));
        _hotKeyExpander.IsExpanded = _hotKeySwitch.IsOn;

        _hotKeyError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        _hotKeyError.IsClosable = false;
        _hotKeyError.Hidden = true;

        AddGroup(Translator.CompanionSettings_ShortcutGroup, _hotKeyExpander, _hotKeyError);
        Bind.Bind(vm, nameof(vm.IsCompanionEnabled), s => s.IsCompanionEnabled, _ => UpdateHotKeyAvailability());

        if (_hotKeySwitch.IsOn && !hotKey.TryConfigure(true, StoredGesture))
            ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
    }

    private HotKeyGesture StoredGesture => new(ViewModel.PreferencesService.CompanionHotKeyKey, ViewModel.PreferencesService.CompanionHotKeyModifiers);

    private void HotKeySwitchToggled()
    {
        if (_updatingHotKeySwitch) return;
        SetHotKeyEnabled(_hotKeySwitch.IsOn);
    }

    private void SetHotKeyEnabled(bool enabled)
    {
        _hotKeyError.Hidden = true;
        if (!hotKey.TryConfigure(enabled, StoredGesture))
        {
            ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
            SetSwitch(ViewModel.PreferencesService.IsCompanionHotKeyEnabled);
            return;
        }
        ViewModel.PreferencesService.IsCompanionHotKeyEnabled = enabled;
        SetSwitch(enabled);
        _hotKeyExpander.IsExpanded = enabled;
        UpdateHotKeyAvailability();
    }

    private void SetSwitch(bool on)
    {
        _updatingHotKeySwitch = true;
        _hotKeySwitch.IsOn = on;
        _updatingHotKeySwitch = false;
    }

    private void RecorderCaptureStarted(object? sender, EventArgs args)
    {
        _hotKeyError.Hidden = true;
        // A registered hotkey never reaches the window as a key press, so the current one is
        // removed while listening (Windows HotKeyInput_CaptureStarted).
        hotKey.Suspend();
    }

    private void RecorderCaptureCanceled(object? sender, EventArgs args)
    {
        _recorder.SetGesture(StoredGesture);
        ResumeStoredHotKey();
    }

    private void RecorderCommitted(object? sender, HotKeyGesture gesture) => Commit(gesture);

    /// <summary>The ✕ in the recorder turns the shortcut off; the stored combination is kept for later.</summary>
    private void RecorderCleared(object? sender, EventArgs args)
    {
        _recorder.SetGesture(StoredGesture);
        SetHotKeyEnabled(false);
    }

    private void Commit(HotKeyGesture gesture)
    {
        _hotKeyError.Hidden = true;
        var candidate = gesture.Normalize();
        if (!candidate.IsValid)
        {
            ShowRefused(candidate, Translator.CompanionSettings_MacOS_HotKey_Invalid);
            return;
        }
        if (!hotKey.TryConfigure(ViewModel.PreferencesService.IsCompanionHotKeyEnabled, candidate))
        {
            ShowRefused(candidate, Translator.CompanionSettings_MacOS_HotKey_Conflict);
            return;
        }
        ViewModel.PreferencesService.CompanionHotKeyKey = candidate.Key;
        ViewModel.PreferencesService.CompanionHotKeyModifiers = candidate.Modifiers;
        _recorder.SetGesture(candidate);
    }

    /// <summary>The refused combination stays in the field in red; the previous shortcut is registered again.</summary>
    private void ShowRefused(HotKeyGesture refused, string message)
    {
        _recorder.ShowRefused(refused);
        ResumeStoredHotKey();
        ShowHotKeyError(message);
    }

    private void ResumeStoredHotKey()
    {
        if (hotKey.TryConfigure(ViewModel.PreferencesService.IsCompanionHotKeyEnabled, StoredGesture)) return;
        ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
    }

    private void ShowHotKeyError(string message)
    {
        _hotKeyError.Message = message;
        _hotKeyError.Hidden = false;
    }

    private void UpdateHotKeyAvailability()
    {
        var companion = ViewModel.IsCompanionEnabled;
        _hotKeyExpander.IsEnabled = companion;
        _hotKeySwitch.IsEnabled = companion;
        var active = companion && _hotKeySwitch.IsOn;
        _recorder.IsEnabled = active;
        _resetButton.Enabled = active;
    }

    #endregion
}
