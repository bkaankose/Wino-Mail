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
/// Wino Assistant (Windows VoiceControlSettingsPage): the switch and Try it, then "How you ask" (shortcuts, microphone,
/// bubble position) and "How Wino answers" (What Wino remembers, run requests without asking). The shortcut logic
/// lives here, like the Windows page code-behind and the Companion page.
/// </summary>
public sealed class VoiceControlSettingsPageViewController(VoiceControlSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
    MacVoiceHotKeyAccess hotKey)
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
    private WinoSettingsExpander _shortcuts = null!;
    private WinoInfoBar _hotKeyError = null!;

    private MacVoiceHotKeyAccess HotKeyAccess => hotKey;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var enabled = Bind.Switch(vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled, (s, v) => s.IsVoiceControlEnabled = v,
            Translator.VoiceControlSettings_Enable_Title);
        var enableCard = Card(Translator.VoiceControlSettings_Enable_Title, Translator.VoiceControlSettings_MacOS_Enable_Description, WinoIconGlyph.None, enabled);
        enableCard.LeadingView = IntelligenceSettingsViews.AppLogo(WinoSettingsStyle.IconSize);

        var tryButton = Bind.Button(Translator.VoiceControlSettings_Try_Button, vm.TryVoiceControlCommand);
        var tryCard = Card(Translator.VoiceControlSettings_Try_Title, Translator.VoiceControlSettings_Try_Description, WinoIconGlyph.Play, tryButton);
        var top = Group(null, enableCard, tryCard);

        var shortcuts = BuildShortcuts();
        var (microphoneCard, microphoneProblem) = BuildMicrophone();
        var position = Bind.PopUp(vm, Positions.Select(PositionTitle).ToList(), nameof(vm.SelectedPosition),
            s => Math.Max(0, Array.IndexOf(Positions, s.SelectedPosition)), (s, index) => s.SelectedPosition = Positions[index], width: 220);
        var positionCard = Card(Translator.VoiceControlSettings_Position_Title, Translator.VoiceControlSettings_MacOS_Position_Description, WinoIconGlyph.Desktop, position);
        var howYouAsk = Group(Translator.VoiceControlSettings_HowYouAsk_Header, shortcuts, _hotKeyError, microphoneCard, microphoneProblem, positionCard);

        var brain = NavigationCard(Translator.VoiceBrain_Title, Translator.VoiceBrain_Description, WinoIconGlyph.Lightbulb, () => vm.OpenVoiceBrainCommand.Execute(null));
        var autoConfirm = Bind.Switch(vm, nameof(vm.IsAutoConfirmEnabled), s => s.IsAutoConfirmEnabled, (s, v) => s.IsAutoConfirmEnabled = v,
            Translator.VoiceControlSettings_AutoConfirm_Title);
        var autoConfirmCard = Card(Translator.VoiceControlSettings_AutoConfirm_Title, Translator.VoiceControlSettings_AutoConfirm_Description, WinoIconGlyph.Shield, autoConfirm);
        var howWinoAnswers = Group(Translator.VoiceControlSettings_HowWinoAnswers_Header, brain, autoConfirmCard);

        foreach (var view in new NSView[] { top, howYouAsk, howWinoAnswers })
            Add(Bind.Visible(view, vm, nameof(vm.IsAvailable), s => s.IsAvailable));

        foreach (var view in new NSView[] { tryCard, microphoneCard, positionCard, autoConfirmCard })
            Bind.Enabled(view, vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled);
    }

    /// <summary>Windows microphone card: the device, a Test toggle with the live level, and the access problem with a link to Privacy settings.</summary>
    private (WinoSettingsCard Card, WinoInfoBar Problem) BuildMicrophone()
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

        var card = Card(Translator.VoiceControlSettings_Microphone_Title, Translator.VoiceControlSettings_Microphone_Description, WinoIconGlyph.Microphone,
            Row(level, devices, test));
        return (card, problem);
    }

    #region Shortcut

    /// <summary>
    /// Windows ShortcutsExpander: one card for speaking and one for typing, each a recorder and a switch. The Mac keeps
    /// its reset button in the row. The error bar sits under the expander.
    /// </summary>
    private WinoSettingsExpander BuildShortcuts()
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

        var voiceCard = _voiceShortcut.Build(Translator.VoiceControlSettings_HotKey_Title, Translator.VoiceControlSettings_HotKey_Description, Translator.VoiceControlSettings_HotKey_KeyCombination);
        var textCard = _textShortcut.Build(Translator.VoiceControlSettings_TextHotKey_Title, Translator.VoiceControlSettings_TextHotKey_Description, Translator.VoiceControlSettings_TextHotKey_KeyCombination);
        _shortcuts = Expander(Translator.VoiceControlSettings_Shortcuts_Title, Translator.VoiceControlSettings_Shortcuts_Description, WinoIconGlyph.Keyboard, null,
            voiceCard, textCard);
        _shortcuts.IsExpanded = true;

        Bind.Bind(vm, nameof(vm.IsVoiceControlEnabled), s => s.IsVoiceControlEnabled, _ => UpdateHotKeyAvailability());
        return _shortcuts;
    }

    private void ShowHotKeyError(string message)
    {
        _hotKeyError.Message = message;
        _hotKeyError.Hidden = false;
    }

    private void UpdateHotKeyAvailability()
    {
        var voice = ViewModel.IsVoiceControlEnabled;
        // The expander re-enables every nested control, so the rows apply their own switch state after it.
        _shortcuts.IsEnabled = voice;
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
        private HotKeyRecorderView _recorder = null!;
        private NSButton _resetButton = null!;
        private bool _updatingSwitch;

        public WinoSettingsCard Build(string title, string description, string keyCombination)
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

            _resetButton.ToolTip = $"{Translator.CompanionSettings_HotKey_ResetToDefault}: {MacHotKeyKeys.Format(defaultGesture)}";
            WinoAccessibility.Label(_recorder, keyCombination);
            // The recorder only has a minimum width; without a fixed width the two rows split their space differently.
            _recorder.WidthAnchor.ConstraintEqualTo(240).Active = true;

            return page.Card(title, description, WinoIconGlyph.None, Row(_recorder, _resetButton, _switch));
        }

        public void UpdateAvailability(bool voiceControlEnabled)
        {
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
