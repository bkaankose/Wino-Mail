using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.Core.VoiceBubble;
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

    private ShortcutRow _voiceShortcut = null!;
    private ShortcutRow _textShortcut = null!;
    private WinoInfoBar _hotKeyError = null!;

    private MacVoiceHotKeyAccess HotKeyAccess => hotKey;

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

    /// <summary>
    /// Windows VoiceHotKeyExpander and TextHotKeyExpander: for speaking and for typing, each a switch, recorder and reset,
    /// with one error bar under both cards.
    /// </summary>
    private WinoSettingsGroup BuildShortcutGroup()
    {
        var vm = ViewModel;
        var preferences = vm.PreferencesService;
        _hotKeyError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        _hotKeyError.IsClosable = false;
        _hotKeyError.Hidden = true;

        _voiceShortcut = new ShortcutRow(this, VoiceBubbleInputMode.Voice, HotKeyGesture.VoiceDefault,
            () => preferences.IsVoiceHotKeyEnabled, value => preferences.IsVoiceHotKeyEnabled = value,
            () => new HotKeyGesture(preferences.VoiceHotKeyKey, preferences.VoiceHotKeyModifiers),
            gesture => { preferences.VoiceHotKeyKey = gesture.Key; preferences.VoiceHotKeyModifiers = gesture.Modifiers; });
        _textShortcut = new ShortcutRow(this, VoiceBubbleInputMode.Text, HotKeyGesture.TextDefault,
            () => preferences.IsVoiceTextHotKeyEnabled, value => preferences.IsVoiceTextHotKeyEnabled = value,
            () => new HotKeyGesture(preferences.VoiceTextHotKeyKey, preferences.VoiceTextHotKeyModifiers),
            gesture => { preferences.VoiceTextHotKeyKey = gesture.Key; preferences.VoiceTextHotKeyModifiers = gesture.Modifiers; });

        var voiceExpander = _voiceShortcut.Build(Translator.VoiceControlSettings_HotKey_Title, Translator.VoiceControlSettings_MacOS_HotKey_Description, Translator.VoiceControlSettings_HotKey_KeyCombination);
        var textExpander = _textShortcut.Build(Translator.VoiceControlSettings_TextHotKey_Title, Translator.VoiceControlSettings_TextHotKey_Description, Translator.VoiceControlSettings_TextHotKey_KeyCombination);

        Bind.Bind(vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled, _ => UpdateHotKeyAvailability());
        return Group(Translator.CompanionSettings_ShortcutGroup, voiceExpander, textExpander, _hotKeyError);
    }

    private void ShowHotKeyError(string message)
    {
        _hotKeyError.Message = message;
        _hotKeyError.Hidden = false;
    }

    private void UpdateHotKeyAvailability()
    {
        var voice = ViewModel.IsVoiceControlEnabled;
        _voiceShortcut.UpdateAvailability(voice);
        _textShortcut.UpdateAvailability(voice);
    }

    /// <summary>One shortcut's switch, recorder and reset; the voice and typing shortcuts behave the same way.</summary>
    private sealed class ShortcutRow(
        VoiceControlSettingsPageViewController page,
        VoiceBubbleInputMode mode,
        HotKeyGesture defaultGesture,
        Func<bool> getEnabled,
        Action<bool> setEnabled,
        Func<HotKeyGesture> getGesture,
        Action<HotKeyGesture> setGesture)
    {
        private WinoLabeledSwitch _switch = null!;
        private WinoSettingsExpander _expander = null!;
        private HotKeyRecorderView _recorder = null!;
        private NSButton _resetButton = null!;
        private bool _updatingSwitch;

        public WinoSettingsExpander Build(string title, string description, string keyCombination)
        {
            _switch = new WinoLabeledSwitch { IsOn = getEnabled() };
            WinoAccessibility.Label(_switch.Switch, title);
            page.Bind.OnActivated(_switch.Switch, () =>
            {
                if (!_updatingSwitch)
                    SetEnabled(_switch.IsOn);
            });

            _recorder = new HotKeyRecorderView();
            _recorder.CaptureStarted += RecorderCaptureStarted;
            _recorder.CaptureCanceled += RecorderCaptureCanceled;
            _recorder.Committed += RecorderCommitted;
            _recorder.Cleared += RecorderCleared;
            page.Bindings.Own(new ActionDisposable(() =>
            {
                _recorder.CaptureStarted -= RecorderCaptureStarted;
                _recorder.CaptureCanceled -= RecorderCaptureCanceled;
                _recorder.Committed -= RecorderCommitted;
                _recorder.Cleared -= RecorderCleared;
                // Leaving the page while listening must not leave the shortcut unregistered.
                if (_recorder.IsRecording) Resume();
            }));
            _recorder.SetGesture(getGesture());

            _resetButton = page.Bind.Button(Translator.Buttons_Reset, () => Commit(defaultGesture));

            _expander = page.Expander(title, description, WinoIconGlyph.Keyboard, _switch,
                page.Card(keyCombination, null, WinoIconGlyph.None, _recorder),
                page.Card(Translator.CompanionSettings_HotKey_ResetToDefault, MacHotKeyKeys.Format(defaultGesture), WinoIconGlyph.None, _resetButton));
            _expander.IsExpanded = _switch.IsOn;
            return _expander;
        }

        public void UpdateAvailability(bool voiceControlEnabled)
        {
            _expander.IsEnabled = voiceControlEnabled;
            _switch.IsEnabled = voiceControlEnabled;
            var active = voiceControlEnabled && _switch.IsOn;
            _recorder.IsEnabled = active;
            _resetButton.Enabled = active;
        }

        private void SetEnabled(bool enabled)
        {
            page._hotKeyError.Hidden = true;
            if (!page.HotKeyAccess.TryConfigure(mode, enabled, getGesture()))
            {
                page.ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
                SetSwitch(getEnabled());
                return;
            }

            setEnabled(enabled);
            SetSwitch(enabled);
            _expander.IsExpanded = enabled;
            page.UpdateHotKeyAvailability();
        }

        private void SetSwitch(bool on)
        {
            _updatingSwitch = true;
            _switch.IsOn = on;
            _updatingSwitch = false;
        }

        private void RecorderCaptureStarted(object? sender, EventArgs args)
        {
            page._hotKeyError.Hidden = true;
            page.HotKeyAccess.Suspend(mode);
        }

        private void RecorderCaptureCanceled(object? sender, EventArgs args)
        {
            _recorder.SetGesture(getGesture());
            Resume();
        }

        private void RecorderCommitted(object? sender, HotKeyGesture gesture) => Commit(gesture);

        /// <summary>The ✕ in the recorder turns the shortcut off; the stored combination is kept for later.</summary>
        private void RecorderCleared(object? sender, EventArgs args)
        {
            _recorder.SetGesture(getGesture());
            SetEnabled(false);
        }

        private void Commit(HotKeyGesture gesture)
        {
            page._hotKeyError.Hidden = true;
            var candidate = gesture.Normalize();
            if (!candidate.IsValid)
            {
                ShowRefused(candidate, Translator.CompanionSettings_MacOS_HotKey_Invalid);
                return;
            }

            if (!page.HotKeyAccess.TryConfigure(mode, getEnabled(), candidate))
            {
                ShowRefused(candidate, Translator.CompanionSettings_MacOS_HotKey_Conflict);
                return;
            }

            setGesture(candidate);
            _recorder.SetGesture(candidate);
        }

        private void ShowRefused(HotKeyGesture refused, string message)
        {
            _recorder.ShowRefused(refused);
            Resume();
            page.ShowHotKeyError(message);
        }

        private void Resume()
        {
            if (page.HotKeyAccess.TryConfigure(mode, getEnabled(), getGesture())) return;
            page.ShowHotKeyError(Translator.CompanionSettings_MacOS_HotKey_Conflict);
        }
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
