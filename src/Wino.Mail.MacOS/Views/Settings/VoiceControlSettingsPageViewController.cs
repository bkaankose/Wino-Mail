using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Infrastructure.Voice;
using Wino.Platform.MacOS.Services;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Voice Control (Windows VoiceControlSettingsPage): what voice control does and how to get it while the Wino Account
/// has no Wino Intelligence, then the switch, microphone, global shortcut, bubble position, auto-confirm, Personal mode
/// and a Try button. The shortcut logic lives here, like the Windows page code-behind and the Companion page.
/// </summary>
public sealed class VoiceControlSettingsPageViewController(VoiceControlSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    MacVoiceHotKeyAccess hotKey, IPlatformCapabilities capabilities)
    : SettingsPageViewController<VoiceControlSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private const string MicrophonePrivacyUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    private static readonly VoiceBubblePosition[] Positions =
    [
        VoiceBubblePosition.BottomRight,
        VoiceBubblePosition.TopRight,
        VoiceBubblePosition.TopLeft,
        VoiceBubblePosition.BottomLeft,
        VoiceBubblePosition.Center
    ];

    private WinoLabeledSwitch _hotKeySwitch = null!;
    private WinoSettingsExpander _hotKeyExpander = null!;
    private HotKeyRecorderView _recorder = null!;
    private NSButton _resetButton = null!;
    private WinoInfoBar _hotKeyError = null!;
    private bool _updatingHotKeySwitch;

    protected override void BuildPage()
    {
        BuildLockedSection();
        BuildAvailableSection();
    }

    /// <summary>Windows promo and upgrade cards, shown while voice commands are not part of the account.</summary>
    private void BuildLockedSection()
    {
        var vm = ViewModel;
        var promo = Group(Translator.VoiceControlSettings_Promo_Title,
            Card(Translator.VoiceControlSettings_MacOS_Promo_MenuBar_Title, Translator.VoiceControlSettings_MacOS_Promo_MenuBar_Description, WinoIconGlyph.Microphone),
            Card(Translator.VoiceControlSettings_Promo_Steps_Title, Translator.VoiceControlSettings_Promo_Steps_Description, WinoIconGlyph.Board),
            Card(Translator.VoiceControlSettings_Promo_Confirm_Title, Translator.VoiceControlSettings_Promo_Confirm_Description, WinoIconGlyph.Shield));
        promo.Footer = Translator.VoiceControlSettings_Promo_Description;

        // App Store builds buy through StoreKit on the Wino Intelligence page; the browser checkout is for the other builds.
        var upgradeButton = capabilities.AppleAppStore
            ? Bind.Button(Translator.VoiceControlSettings_Upgrade_Button, vm.SeeWhatsIncludedCommand, primary: true)
            : Bind.Button(Translator.VoiceControlSettings_Upgrade_Button, vm.GetWinoIntelligenceCommand, primary: true);
        var whatsIncluded = Bind.Button(Translator.WinoAccount_Management_SeeWhatsIncluded, vm.SeeWhatsIncludedCommand);
        var upgrade = Group(null, Card(Translator.VoiceControlSettings_Upgrade_Title, Translator.VoiceControlSettings_Upgrade_Description,
            WinoIconGlyph.WinoIntelligence, Row(whatsIncluded, upgradeButton)));

        Add(Bind.Visible(promo, vm, nameof(vm.IsLocked), s => s.IsLocked));
        Add(Bind.Visible(upgrade, vm, nameof(vm.IsLocked), s => s.IsLocked));
    }

    private void BuildAvailableSection()
    {
        var vm = ViewModel;

        var enabled = Bind.Switch(vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled, (s, v) => s.IsVoiceControlEnabled = v,
            Translator.VoiceControlSettings_Enable_Title);
        var enable = Group(null, Card(Translator.VoiceControlSettings_Enable_Title, Translator.VoiceControlSettings_MacOS_Enable_Description, WinoIconGlyph.Microphone, enabled));

        var microphone = BuildMicrophoneGroup();
        var shortcut = BuildShortcutGroup();

        var position = Bind.PopUp(vm, Positions.Select(PositionTitle).ToList(), nameof(vm.SelectedPosition),
            s => Math.Max(0, Array.IndexOf(Positions, s.SelectedPosition)), (s, index) => s.SelectedPosition = Positions[index], width: 220);
        var positionGroup = Group(null, Card(Translator.VoiceControlSettings_Position_Title, Translator.VoiceControlSettings_MacOS_Position_Description, WinoIconGlyph.Desktop, position));

        var autoConfirm = Bind.Switch(vm, nameof(vm.IsAutoConfirmEnabled), s => s.IsAutoConfirmEnabled, (s, v) => s.IsAutoConfirmEnabled = v,
            Translator.VoiceControlSettings_AutoConfirm_Title);
        var autoConfirmGroup = Group(null, Card(Translator.VoiceControlSettings_AutoConfirm_Title, Translator.VoiceControlSettings_AutoConfirm_Description, WinoIconGlyph.Shield, autoConfirm));

        var personal = Bind.Switch(vm, nameof(vm.IsPersonalMode), s => s.IsPersonalMode, (s, v) => s.IsPersonalMode = v,
            Translator.VoiceControlSettings_PersonalMode_Title);
        var modeFailed = InfoBar(WinoInfoBarSeverity.Error, null, Translator.VoiceControlSettings_PersonalMode_Failed);
        modeFailed.IsClosable = false;
        var brain = NavigationCard(Translator.VoiceBrain_Title, Translator.VoiceBrain_Description, WinoIconGlyph.Lightbulb, () => vm.OpenVoiceBrainCommand.Execute(null));
        var personalGroup = Group(null,
            Card(Translator.VoiceControlSettings_PersonalMode_Title, Translator.VoiceControlSettings_PersonalMode_Description, WinoIconGlyph.Person, personal),
            Bind.Visible(brain, vm, nameof(vm.IsPersonalMode), s => s.IsPersonalMode));

        var tryButton = Bind.Button(Translator.VoiceControlSettings_Try_Button, vm.TryVoiceControlCommand);
        var tryGroup = Group(null, Card(Translator.VoiceControlSettings_Try_Title, Translator.VoiceControlSettings_Try_Description, WinoIconGlyph.Play, tryButton));

        foreach (var view in new NSView[] { enable, microphone, shortcut, positionGroup, autoConfirmGroup, personalGroup })
            Add(Bind.Visible(view, vm, nameof(vm.IsAvailable), s => s.IsAvailable));
        Add(Bind.Visible(modeFailed, vm, nameof(vm.IsModeChangeFailed), s => s.IsAvailable && s.IsModeChangeFailed));
        Add(Bind.Visible(tryGroup, vm, nameof(vm.IsAvailable), s => s.IsAvailable));

        foreach (var view in new NSView[] { microphone, positionGroup, autoConfirmGroup, tryGroup })
            Bind.Enabled(view, vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled);
    }

    /// <summary>Windows microphone card: the device, a Test toggle with the live level, and the access problem with a link to Privacy settings.</summary>
    private WinoSettingsGroup BuildMicrophoneGroup()
    {
        var vm = ViewModel;
        var devices = Bind.PopUp<VoiceControlSettingsPageViewModel, MicrophoneOption>(vm,
            s => s.Microphones, option => option.DisplayName,
            nameof(vm.SelectedMicrophone), s => s.SelectedMicrophone, (s, v) => s.SelectedMicrophone = v, width: 240,
            itemsCollection: vm.Microphones);
        WinoAccessibility.Label(devices, Translator.VoiceControlSettings_Microphone_Title);

        var test = SettingsBinder.CreateButton(Translator.VoiceControlSettings_Microphone_Test);
        test.SetButtonType(NSButtonType.PushOnPushOff);
        Bind.Bind(vm, nameof(vm.IsTestingMicrophone), s => s.IsTestingMicrophone, on => test.State = on ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind.OnActivated(test, () => vm.IsTestingMicrophone = test.State == NSCellStateValue.On);

        var level = new NSLevelIndicator
        {
            LevelIndicatorStyle = NSLevelIndicatorStyle.ContinuousCapacity,
            MinValue = 0,
            MaxValue = 1,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(level, 120, -1);
        WinoAccessibility.Label(level, Translator.VoiceControlSettings_Microphone_Level);
        Bind.Bind(vm, nameof(vm.MicrophoneLevel), s => s.MicrophoneLevel, value => level.DoubleValue = value);
        Bind.Visible(level, vm, nameof(vm.IsTestingMicrophone), s => s.IsTestingMicrophone);

        var problem = InfoBar(WinoInfoBarSeverity.Warning, null, null);
        problem.IsClosable = false;
        problem.ActionTitle = Translator.VoiceControlSettings_Microphone_OpenPrivacySettings;
        problem.ActionInvoked += (_, _) => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(MicrophonePrivacyUrl));
        Bind.Bind(vm, nameof(vm.MicrophoneProblemMessage), s => s.MicrophoneProblemMessage, message => problem.Message = message);
        Bind.Bind(vm, nameof(vm.IsMicrophoneAccessDenied), s => s.IsMicrophoneAccessDenied,
            denied => problem.ActionTitle = denied ? Translator.VoiceControlSettings_Microphone_OpenPrivacySettings : null);
        Bind.Visible(problem, vm, nameof(vm.HasMicrophoneProblem), s => s.HasMicrophoneProblem);

        return Group(null,
            Card(Translator.VoiceControlSettings_Microphone_Title, Translator.VoiceControlSettings_Microphone_Description, WinoIconGlyph.Microphone,
                Row(level, devices, test)),
            problem);
    }

    #region Shortcut

    /// <summary>Windows VoiceHotKeyExpander: switch, recorder, reset, and the error bar under the card.</summary>
    private WinoSettingsGroup BuildShortcutGroup()
    {
        var vm = ViewModel;
        _hotKeySwitch = new WinoLabeledSwitch { IsOn = vm.PreferencesService.IsVoiceHotKeyEnabled };
        WinoAccessibility.Label(_hotKeySwitch.Switch, Translator.VoiceControlSettings_HotKey_Title);
        Bind.OnActivated(_hotKeySwitch.Switch, () =>
        {
            if (!_updatingHotKeySwitch)
                SetHotKeyEnabled(_hotKeySwitch.IsOn);
        });

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

        _resetButton = Bind.Button(Translator.Buttons_Reset, () => Commit(HotKeyGesture.VoiceDefault));

        _hotKeyExpander = Expander(Translator.VoiceControlSettings_HotKey_Title, Translator.VoiceControlSettings_MacOS_HotKey_Description, WinoIconGlyph.Keyboard, _hotKeySwitch,
            Card(Translator.VoiceControlSettings_HotKey_KeyCombination, null, WinoIconGlyph.None, _recorder),
            Card(Translator.CompanionSettings_HotKey_ResetToDefault, MacHotKeyKeys.Format(HotKeyGesture.VoiceDefault), WinoIconGlyph.None, _resetButton));
        _hotKeyExpander.IsExpanded = _hotKeySwitch.IsOn;

        _hotKeyError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        _hotKeyError.IsClosable = false;
        _hotKeyError.Hidden = true;

        Bind.Bind(vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled, _ => UpdateHotKeyAvailability());
        return Group(Translator.CompanionSettings_ShortcutGroup, _hotKeyExpander, _hotKeyError);
    }

    private HotKeyGesture StoredGesture => new(ViewModel.PreferencesService.VoiceHotKeyKey, ViewModel.PreferencesService.VoiceHotKeyModifiers);

    private void SetHotKeyEnabled(bool enabled)
    {
        _hotKeyError.Hidden = true;
        if (!hotKey.TryConfigure(enabled, StoredGesture))
        {
            ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
            SetSwitch(ViewModel.PreferencesService.IsVoiceHotKeyEnabled);
            return;
        }

        ViewModel.PreferencesService.IsVoiceHotKeyEnabled = enabled;
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

        if (!hotKey.TryConfigure(ViewModel.PreferencesService.IsVoiceHotKeyEnabled, candidate))
        {
            ShowRefused(candidate, Translator.CompanionSettings_MacOS_HotKey_Conflict);
            return;
        }

        ViewModel.PreferencesService.VoiceHotKeyKey = candidate.Key;
        ViewModel.PreferencesService.VoiceHotKeyModifiers = candidate.Modifiers;
        _recorder.SetGesture(candidate);
    }

    private void ShowRefused(HotKeyGesture refused, string message)
    {
        _recorder.ShowRefused(refused);
        ResumeStoredHotKey();
        ShowHotKeyError(message);
    }

    private void ResumeStoredHotKey()
    {
        if (hotKey.TryConfigure(ViewModel.PreferencesService.IsVoiceHotKeyEnabled, StoredGesture)) return;
        ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
    }

    private void ShowHotKeyError(string message)
    {
        _hotKeyError.Message = message;
        _hotKeyError.Hidden = false;
    }

    private void UpdateHotKeyAvailability()
    {
        var voice = ViewModel.IsVoiceControlEnabled;
        _hotKeyExpander.IsEnabled = voice;
        _hotKeySwitch.IsEnabled = voice;
        var active = voice && _hotKeySwitch.IsOn;
        _recorder.IsEnabled = active;
        _resetButton.Enabled = active;
    }

    #endregion

    /// <summary>On the Mac the bottom right position is the companion's place under the menu bar icon.</summary>
    private static string PositionTitle(VoiceBubblePosition position) => position switch
    {
        VoiceBubblePosition.BottomRight => Translator.VoiceControlSettings_MacOS_Position_MenuBar,
        VoiceBubblePosition.TopRight => Translator.VoiceControlSettings_Position_TopRight,
        VoiceBubblePosition.TopLeft => Translator.VoiceControlSettings_Position_TopLeft,
        VoiceBubblePosition.BottomLeft => Translator.VoiceControlSettings_Position_BottomLeft,
        _ => Translator.VoiceControlSettings_Position_Center
    };
}
