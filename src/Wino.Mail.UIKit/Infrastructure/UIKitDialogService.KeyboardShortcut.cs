using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels.Data;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>The keyboard shortcut editor (Windows KeyboardShortcutDialog, the Mac shortcut sheet).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>
    /// App mode, action, then the key combination. The combination field records a hardware key press (iPad or an
    /// iPhone with a keyboard attached) with its modifiers; without a hardware keyboard it takes one letter or digit
    /// from the on-screen keyboard and the modifier toggles below it supply the modifiers. Same validation and result
    /// as the Mac: a key is required, an action is required, the shared service rejects reserved and used combinations.
    /// </summary>
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null) => PresentAsync(async presenter =>
    {
        var shortcuts = services.GetService<IKeyboardShortcutService>();
        var modes = new[] { WinoApplicationMode.Mail, WinoApplicationMode.Calendar, WinoApplicationMode.Contacts, WinoApplicationMode.Tasks };
        var modeNames = new[] { Translator.KeyboardShortcuts_ModeMail, Translator.KeyboardShortcuts_ModeCalendar, Translator.ContactsPage_Title, Translator.ToDoPage_Title };

        var form = new FormSheet(existingShortcut is null ? Translator.KeyboardShortcuts_Add : Translator.KeyboardShortcuts_EditTitle,
            Translator.KeyboardShortcuts_FocusArea, Translator.Buttons_Save) { PrefersLargeDetent = true };
        var modePicker = form.AddPopUp(Translator.KeyboardShortcuts_Mode, modeNames, existingShortcut is null ? 0 : Math.Max(0, Array.IndexOf(modes, existingShortcut.Mode)));
        var actionPicker = form.AddPopUp(Translator.KeyboardShortcuts_Action, new List<string>(), -1);
        List<KeyboardShortcutActionViewModel> actions = [];

        WinoApplicationMode SelectedMode() => modes[Math.Clamp(modePicker.SelectedIndex, 0, modes.Length - 1)];

        void RefreshActions(KeyboardShortcutAction selected)
        {
            var mode = SelectedMode();
            actions = ActionsFor(mode).Select(action => new KeyboardShortcutActionViewModel(mode, action)).ToList();
            actionPicker.SetItems(actions.Select(action => action.DisplayName), Math.Max(0, actions.FindIndex(action => action.Action == selected)));
        }
        RefreshActions(existingShortcut?.Action ?? KeyboardShortcutAction.None);
        modePicker.Changed += (_, _) => RefreshActions(KeyboardShortcutAction.None);

        var recorder = new ShortcutRecorderView();
        var modifiers = new ModifierToggles();
        recorder.Recorded += (_, flags) => modifiers.Modifiers = flags;
        recorder.ModifiersProvider = () => modifiers.Modifiers;
        modifiers.Changed += (_, _) => recorder.SetShortcut(recorder.Key, modifiers.Modifiers);
        if (existingShortcut is not null)
        {
            modifiers.Modifiers = existingShortcut.ModifierKeys;
            recorder.SetShortcut(existingShortcut.Key, existingShortcut.ModifierKeys);
        }
        form.AddRow(Translator.KeyboardShortcuts_KeyCombination, recorder);
        form.AddRow(Translator.KeyboardShortcuts_Modifiers, modifiers);

        var result = KeyboardShortcutDialogResult.Canceled();
        form.ConfirmAsync = async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(recorder.Key)) return Translator.KeyboardShortcuts_EnterKey;
                var mode = SelectedMode();
                var index = actionPicker.SelectedIndex;
                if (index < 0 || index >= actions.Count || actions[index].Action == KeyboardShortcutAction.None) return Translator.KeyboardShortcuts_SelectOperation;
                var flags = modifiers.Modifiers;
                var candidate = new KeyboardShortcut { Mode = mode, Key = recorder.Key, ModifierKeys = flags, Action = actions[index].Action };
                if (shortcuts is not null)
                {
                    if (!shortcuts.IsShortcutAllowed(candidate)) return Translator.KeyboardShortcuts_InvalidShortcut;
                    if (await shortcuts.IsKeyCombinationInUseAsync(mode, recorder.Key, flags, existingShortcut?.Id)) return Translator.KeyboardShortcuts_ShortcutInUse;
                }
                result = KeyboardShortcutDialogResult.Success(mode, recorder.Key, flags, actions[index].Action);
                return null;
            }
            catch (Exception exception)
            {
                Serilog.Log.Warning(exception, "Could not validate the keyboard shortcut.");
                return Translator.KeyboardShortcuts_FailedToSave;
            }
        };

        return await form.PresentAsync(presenter) ? result : KeyboardShortcutDialogResult.Canceled();
    }, KeyboardShortcutDialogResult.Canceled());

    /// <summary>The actions offered per mode (same lists as the Mac).</summary>
    private static KeyboardShortcutAction[] ActionsFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail =>
        [
            KeyboardShortcutAction.NewMail, KeyboardShortcutAction.ToggleReadUnread, KeyboardShortcutAction.ToggleFlag,
            KeyboardShortcutAction.ToggleArchive, KeyboardShortcutAction.Delete, KeyboardShortcutAction.Move,
            KeyboardShortcutAction.Reply, KeyboardShortcutAction.ReplyAll, KeyboardShortcutAction.Send
        ],
        WinoApplicationMode.Calendar => [KeyboardShortcutAction.NewEvent, KeyboardShortcutAction.Delete],
        _ => Enum.GetValues<KeyboardShortcutAction>().Where(action => action != KeyboardShortcutAction.None).ToArray()
    };

    /// <summary>Text for a combination with the Mac modifier symbols, for example "⌃⇧ D".</summary>
    private static string ShortcutText(string key, ModifierKeys modifiers)
        => (modifiers.HasFlag(ModifierKeys.Control) ? "⌃" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "⌥" : "")
           + (modifiers.HasFlag(ModifierKeys.Shift) ? "⇧" : "") + (modifiers.HasFlag(ModifierKeys.Command) ? "⌘" : "") + " " + key;

    /// <summary>
    /// A field that records one key combination. A hardware key press (<see cref="UIKey"/>) gives the key and its
    /// modifiers; the on-screen keyboard (UIKeyInput) gives one letter or digit and the modifier toggles give the rest.
    /// </summary>
    private sealed class ShortcutRecorderView : UIView, IUIKeyInput
    {
        private readonly UILabel _label;
        private readonly WinoSurfaceView _surface;
        private DateTime _lastHardwarePress = DateTime.MinValue;

        public ShortcutRecorderView()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            _surface = new WinoSurfaceView { Fill = UIColor.SecondarySystemGroupedBackground, Stroke = WinoStyle.GroupStroke, StrokeWidth = 1, CornerRadius = WinoStyle.ControlRadius };
            WinoLayout.Fill(_surface, this);
            _label = WinoStyle.Label(Translator.KeyboardShortcuts_PressKeysHere, WinoStyle.Body, WinoStyle.TertiaryText);
            _label.TextAlignment = UITextAlignment.Center;
            WinoLayout.Fill(_label, this, 8, 12, 8, 12);
            HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            AddGestureRecognizer(new UITapGestureRecognizer(() => BecomeFirstResponder()));
            IsAccessibilityElement = true;
            AccessibilityLabel = Translator.KeyboardShortcuts_KeyCombination;
            AccessibilityHint = Translator.KeyboardShortcuts_FocusArea;
            AccessibilityTraits = UIAccessibilityTrait.Button;
        }

        public string Key { get; private set; } = string.Empty;

        /// <summary>The modifiers a key from the on-screen keyboard is recorded with.</summary>
        public Func<ModifierKeys>? ModifiersProvider { get; set; }

        /// <summary>Raised with the modifiers of a hardware key press.</summary>
        public event EventHandler<ModifierKeys>? Recorded;

        public override bool CanBecomeFirstResponder => true;

        public override bool BecomeFirstResponder()
        {
            var became = base.BecomeFirstResponder();
            if (became) { _surface.Stroke = WinoStyle.Accent; _surface.StrokeWidth = 2; }
            return became;
        }

        public override bool ResignFirstResponder()
        {
            var resigned = base.ResignFirstResponder();
            if (resigned) { _surface.Stroke = WinoStyle.GroupStroke; _surface.StrokeWidth = 1; }
            return resigned;
        }

        public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
        {
            var handled = false;
            foreach (var press in presses)
            {
                if (press.Key is not { } key || UIKitShortcutKeys.KeyName(key) is not { } name) continue;
                _lastHardwarePress = DateTime.UtcNow;
                var flags = UIKitShortcutKeys.Modifiers(key.ModifierFlags);
                SetShortcut(name, flags);
                Recorded?.Invoke(this, flags);
                handled = true;
            }
            if (!handled) base.PressesBegan(presses, evt);
        }

        // ---- UIKeyInput (the on-screen keyboard) ----

        [Export("hasText")]
        public bool HasText => Key.Length > 0;

        [Export("insertText:")]
        public void InsertText(string text)
        {
            if (FromHardware || string.IsNullOrEmpty(text)) return;
            if (UIKitShortcutKeys.KeyName(text) is { } name) SetShortcut(name, ModifiersProvider?.Invoke() ?? ModifierKeys.None);
        }

        [Export("deleteBackward")]
        public void DeleteBackward()
        {
            if (!FromHardware) SetShortcut(string.Empty, ModifiersProvider?.Invoke() ?? ModifierKeys.None);
        }

        [Export("autocorrectionType")]
        public UITextAutocorrectionType AutocorrectionType => UITextAutocorrectionType.No;

        [Export("autocapitalizationType")]
        public UITextAutocapitalizationType AutocapitalizationType => UITextAutocapitalizationType.AllCharacters;

        [Export("spellCheckingType")]
        public UITextSpellCheckingType SpellCheckingType => UITextSpellCheckingType.No;

        [Export("keyboardType")]
        public UIKeyboardType KeyboardType => UIKeyboardType.AsciiCapable;

        /// <summary>A hardware press also reaches UIKeyInput; the press already recorded it.</summary>
        private bool FromHardware => DateTime.UtcNow - _lastHardwarePress < TimeSpan.FromMilliseconds(300);

        public void SetShortcut(string? key, ModifierKeys modifiers)
        {
            Key = key ?? string.Empty;
            if (Key.Length == 0)
            {
                _label.Text = Translator.KeyboardShortcuts_PressKeysHere;
                _label.TextColor = WinoStyle.TertiaryText;
                AccessibilityValue = null;
                return;
            }
            _label.Text = ShortcutText(Key, modifiers);
            _label.TextColor = WinoStyle.PrimaryText;
            AccessibilityValue = _label.Text;
        }
    }

    /// <summary>Four toggle buttons (⌃ ⌥ ⇧ ⌘) for the modifiers of a combination typed without a hardware keyboard.</summary>
    private sealed class ModifierToggles : UIStackView
    {
        private static readonly (ModifierKeys Flag, string Symbol, string Name)[] Keys =
        [
            (ModifierKeys.Control, "⌃", "Control"),
            (ModifierKeys.Alt, "⌥", "Option"),
            (ModifierKeys.Shift, "⇧", "Shift"),
            (ModifierKeys.Command, "⌘", "Command")
        ];

        private readonly List<(ModifierKeys Flag, UIButton Button)> _buttons = [];
        private ModifierKeys _modifiers;

        public ModifierToggles()
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Axis = UILayoutConstraintAxis.Horizontal;
            Spacing = (nfloat)WinoStyle.Space2;
            Distribution = UIStackViewDistribution.FillEqually;
            foreach (var (flag, _, name) in Keys)
            {
                var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
                button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
                button.AccessibilityLabel = name;
                button.PrimaryActionTriggered += (_, _) =>
                {
                    _modifiers ^= flag;
                    Apply();
                    Changed?.Invoke(this, EventArgs.Empty);
                };
                _buttons.Add((flag, button));
                AddArrangedSubview(button);
            }
            Apply();
        }

        public event EventHandler? Changed;

        public ModifierKeys Modifiers
        {
            get => _modifiers;
            set { _modifiers = value; Apply(); }
        }

        private void Apply()
        {
            foreach (var (flag, button) in _buttons)
            {
                var on = _modifiers.HasFlag(flag);
                var configuration = on ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
                configuration.Title = Keys.First(key => key.Flag == flag).Symbol;
                configuration.TitleLineBreakMode = UILineBreakMode.Clip;
                button.Configuration = configuration;
                button.Selected = on;
                button.AccessibilityTraits = on ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
            }
        }
    }
}

/// <summary>
/// The key names iOS keyboard shortcuts are stored with; the same names as the Mac ShortcutKeys (Enter, Tab, Space,
/// Back, Delete, Escape, arrows, letters, NumberN), so a shortcut recorded on one Apple platform means the same key on
/// the other. Letters and digits use the typed character (layout aware), not the HID usage.
/// </summary>
internal static class UIKitShortcutKeys
{
    internal static string? KeyName(UIKey key)
    {
        switch (key.KeyCode)
        {
            case UIKeyboardHidUsage.KeyboardReturnOrEnter:
            case UIKeyboardHidUsage.KeypadEnter: return "Enter";
            case UIKeyboardHidUsage.KeyboardTab: return "Tab";
            case UIKeyboardHidUsage.KeyboardSpacebar: return "Space";
            case UIKeyboardHidUsage.KeyboardDeleteOrBackspace: return "Back";
            case UIKeyboardHidUsage.KeyboardDeleteForward: return "Delete";
            case UIKeyboardHidUsage.KeyboardEscape: return "Escape";
            case UIKeyboardHidUsage.KeyboardLeftArrow: return "Left";
            case UIKeyboardHidUsage.KeyboardRightArrow: return "Right";
            case UIKeyboardHidUsage.KeyboardDownArrow: return "Down";
            case UIKeyboardHidUsage.KeyboardUpArrow: return "Up";
        }
        return KeyName(key.CharactersIgnoringModifiers);
    }

    /// <summary>A typed character: a letter, a digit, space, return or tab; null for anything else.</summary>
    internal static string? KeyName(string? characters)
    {
        if (string.IsNullOrEmpty(characters)) return null;
        var character = char.ToUpperInvariant(characters[0]);
        if (character == ' ') return "Space";
        if (character is '\n' or '\r') return "Enter";
        if (character == '\t') return "Tab";
        if (char.IsLetter(character)) return character.ToString();
        if (char.IsDigit(character)) return "Number" + character;
        return null;
    }

    internal static ModifierKeys Modifiers(UIKeyModifierFlags flags)
    {
        var modifiers = ModifierKeys.None;
        if (flags.HasFlag(UIKeyModifierFlags.Command)) modifiers |= ModifierKeys.Command;
        if (flags.HasFlag(UIKeyModifierFlags.Control)) modifiers |= ModifierKeys.Control;
        if (flags.HasFlag(UIKeyModifierFlags.Alternate)) modifiers |= ModifierKeys.Alt;
        if (flags.HasFlag(UIKeyModifierFlags.Shift)) modifiers |= ModifierKeys.Shift;
        return modifiers;
    }
}
