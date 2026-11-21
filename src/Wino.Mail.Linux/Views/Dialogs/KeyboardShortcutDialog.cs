using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// Add or edit a keyboard shortcut (Windows KeyboardShortcutDialog): mode, action and a recorder that captures the key
/// combination while it is armed, validated in the Windows order when Save is pressed. Keys are named by
/// <see cref="ShortcutKeys"/>, so what is recorded is what the router matches.
/// </summary>
internal sealed class KeyboardShortcutDialog
{
    private static readonly WinoApplicationMode[] Modes = [WinoApplicationMode.Mail, WinoApplicationMode.Calendar, WinoApplicationMode.Contacts, WinoApplicationMode.Tasks];

    private readonly IKeyboardShortcutService? _shortcuts;
    private readonly Gtk.Application? _application;
    private readonly Guid? _existingId;
    private readonly FormDialog _form;
    private readonly Adw.ComboRow _mode;
    private readonly Adw.ComboRow _action;
    private readonly Gtk.Button _recorder;
    private readonly Gtk.Stack _recorderState;
    private readonly Gtk.Box _recorded = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private KeyboardShortcutAction[] _actions = [];
    private List<(string Action, string[] Accels)>? _suspendedAccels;
    private string _key = string.Empty;
    private ModifierKeys _modifiers;
    private bool _listening;
    private bool _refilling;

    public KeyboardShortcutDialog(KeyboardShortcut? existing, IKeyboardShortcutService? shortcuts, Gtk.Application? application)
    {
        _shortcuts = shortcuts;
        _application = application;
        _existingId = existing?.Id;

        _form = new FormDialog(existing is null ? Translator.KeyboardShortcuts_Add : Translator.KeyboardShortcuts_EditTitle,
            Translator.KeyboardShortcuts_FocusArea, Translator.Buttons_Save);
        var group = _form.AddGroup();

        _mode = Adw.ComboRow.New();
        _mode.SetUseMarkup(false);
        _mode.SetTitle(Translator.KeyboardShortcuts_Mode);
        _mode.SetModel(Gtk.StringList.New([Translator.KeyboardShortcuts_ModeMail, Translator.KeyboardShortcuts_ModeCalendar,
            Translator.KeyboardShortcuts_ModeContacts, Translator.KeyboardShortcuts_ModeTasks]));
        group.Add(_mode);

        _action = Adw.ComboRow.New();
        _action.SetUseMarkup(false);
        _action.SetTitle(Translator.KeyboardShortcuts_Action);
        group.Add(_action);

        _recorderState = Gtk.Stack.New();
        _recorderState.AddNamed(Gtk.Label.New(Translator.KeyboardShortcuts_PressKeysHere), "empty");
        _recorderState.AddNamed(Gtk.Label.New(Translator.CompanionSettings_HotKey_Listening), "listening");
        _recorderState.AddNamed(_recorded, "recorded");
        _recorder = Gtk.Button.New();
        _recorder.SetChild(_recorderState);
        _recorder.SetValign(Gtk.Align.Center);
        _recorder.SetSizeRequest(160, -1);
        WinoA11y.Label(_recorder, Translator.KeyboardShortcuts_KeyCombination);

        var clear = Gtk.Button.NewFromIconName("edit-clear-symbolic");
        clear.AddCssClass("flat");
        clear.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(clear, Translator.Buttons_Delete);

        var keyRow = Adw.ActionRow.New();
        keyRow.SetUseMarkup(false);
        keyRow.SetTitle(Translator.KeyboardShortcuts_KeyCombination);
        keyRow.AddSuffix(_recorder);
        keyRow.AddSuffix(clear);
        keyRow.SetActivatableWidget(_recorder);
        group.Add(keyRow);

        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _recorder.AddController(keys);
        _form.Own(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h,
            (_, args) => HandleKey(args.Keyval, args.State)));
        _form.Own(GtkSignals.OnClicked(_recorder, () => SetListening(true)));
        _form.Own(GtkSignals.OnClicked(clear, () =>
        {
            SetListening(false);
            _key = string.Empty;
            _modifiers = ModifierKeys.None;
            ShowRecording();
        }));
        _form.Own(GtkSignals.OnNotify(_recorder, "has-focus", () =>
        {
            if (!_recorder.HasFocus) SetListening(false);
        }));
        _form.Own(GtkSignals.OnNotify(_mode, "selected", () =>
        {
            if (!_refilling) FillActions(null);
        }));
        _form.Own(new ActionDisposable(() => SetListening(false)));

        var mode = existing?.Mode ?? WinoApplicationMode.Mail;
        _refilling = true;
        _mode.SetSelected((uint)Math.Max(0, Array.IndexOf(Modes, mode)));
        _refilling = false;
        FillActions(existing?.Action);

        // A shortcut that came from a Mac (Command) cannot fire here; editing it records a new combination.
        if (existing is not null && !existing.ModifierKeys.HasFlag(ModifierKeys.Command))
        {
            _key = ShortcutKeys.Normalize(existing.Key);
            _modifiers = existing.ModifierKeys;
        }
        ShowRecording();

        _form.ValidateAsync = ValidateAsync;
        _form.SetInitialFocus(existing is null ? _mode : _recorder);
#if DEBUG
        DebugCurrent = this;
        _form.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
        }));
#endif
    }

#if DEBUG
    internal static KeyboardShortcutDialog? DebugCurrent { get; private set; }

    internal void DebugSelect(WinoApplicationMode mode, KeyboardShortcutAction action)
    {
        _mode.SetSelected((uint)Math.Max(0, Array.IndexOf(Modes, mode)));
        var index = Array.IndexOf(_actions, action);
        if (index >= 0) _action.SetSelected((uint)index);
    }

    internal void DebugArm() => SetListening(true);

    internal void DebugConfirm() => _form.DebugConfirm();

    internal void DebugCancel() => _form.Dialog.Close();

    internal string DebugState() => $"{SelectedMode}|{SelectedAction}|{_key}|{_modifiers}|listening={_listening}|error={_form.ErrorText}|quit={string.Join(',', _application?.GetAccelsForAction("app.quit") ?? [])}";
#endif

    private WinoApplicationMode SelectedMode => Modes[Math.Min((int)_mode.GetSelected(), Modes.Length - 1)];

    private KeyboardShortcutAction SelectedAction
    {
        get
        {
            var index = _action.GetSelected();
            return index < _actions.Length ? _actions[index] : KeyboardShortcutAction.None;
        }
    }

    /// <summary>Main thread, once.</summary>
    public async Task<KeyboardShortcutDialogResult> PresentAsync(Gtk.Widget owner)
    {
        var confirmed = await _form.PresentAsync(owner);
        return confirmed
            ? KeyboardShortcutDialogResult.Success(SelectedMode, _key, _modifiers, SelectedAction)
            : KeyboardShortcutDialogResult.Canceled();
    }

    /// <summary>The recorder's key path: true when the key was consumed.</summary>
    internal bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        if (!_listening) return false;
        var modifiers = ShortcutKeys.Modifiers(state);
        var name = Gdk.Functions.KeyvalName(keyval);
        if (name == "Escape" && modifiers == ModifierKeys.None)
        {
            SetListening(false);
            return true;
        }
        // The recorder must not trap keyboard users: Tab still moves the focus on.
        if (name is "Tab" or "ISO_Left_Tab" && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            SetListening(false);
            return false;
        }
        if (ShortcutKeys.KeyName(keyval) is not { } key) return true;

        _key = key;
        _modifiers = modifiers;
        SetListening(false);
        ShowRecording();
        _form.ShowError(null);
        _form.Refresh();
        return true;
    }

    private void FillActions(KeyboardShortcutAction? preferred)
    {
        var mode = SelectedMode;
        _actions = ActionsFor(mode);
        _refilling = true;
        try
        {
            _action.SetModel(Gtk.StringList.New(_actions.Select(action => new KeyboardShortcutActionViewModel(mode, action).DisplayName).ToArray()));
            var index = preferred is { } wanted ? Array.IndexOf(_actions, wanted) : -1;
            _action.SetSelected((uint)Math.Max(0, index));
        }
        finally { _refilling = false; }
    }

    private void SetListening(bool listening)
    {
        if (_listening == listening) return;
        _listening = listening;
        if (listening)
        {
            _recorder.AddCssClass("suggested-action");
            SuspendAccelerators();
        }
        else
        {
            _recorder.RemoveCssClass("suggested-action");
            RestoreAccelerators();
        }
        ShowRecording();
    }

    private void ShowRecording()
    {
        if (_listening)
        {
            _recorderState.SetVisibleChildName("listening");
            return;
        }
        while (_recorded.GetFirstChild() is { } child) _recorded.Remove(child);
        if (string.IsNullOrEmpty(_key))
        {
            _recorderState.SetVisibleChildName("empty");
            WinoA11y.Label(_recorder, Translator.KeyboardShortcuts_KeyCombination);
            return;
        }
        _recorded.Append(ShortcutDisplay.Create(_modifiers, _key));
        _recorderState.SetVisibleChildName("recorded");
        WinoA11y.Label(_recorder, $"{Translator.KeyboardShortcuts_KeyCombination}: {ShortcutDisplay.Text(_modifiers, _key)}");
    }

    /// <summary>
    /// Application accelerators are handled by the window before the recorder sees the key, so Ctrl+Q would quit the app
    /// while recording. They are taken away while the recorder listens and given back when it stops.
    /// </summary>
    private void SuspendAccelerators()
    {
        if (_application is null || _suspendedAccels is not null) return;
        var suspended = new List<(string, string[])>();
        foreach (var action in _application.ListActionDescriptions())
        {
            var accels = _application.GetAccelsForAction(action);
            if (accels.Length == 0) continue;
            suspended.Add((action, accels));
            _application.SetAccelsForAction(action, []);
        }
        _suspendedAccels = suspended;
    }

    private void RestoreAccelerators()
    {
        if (_application is null || _suspendedAccels is not { } suspended) return;
        _suspendedAccels = null;
        foreach (var (action, accels) in suspended) _application.SetAccelsForAction(action, accels);
    }

    private async Task<string?> ValidateAsync()
    {
        SetListening(false);
        try
        {
            if (string.IsNullOrWhiteSpace(_key)) return Translator.KeyboardShortcuts_EnterKey;
            var action = SelectedAction;
            if (action == KeyboardShortcutAction.None) return Translator.KeyboardShortcuts_SelectOperation;

            if (_application is not null && ShortcutKeys.Accelerator(_modifiers, _key) is { } accelerator
                && _application.GetActionsForAccel(accelerator).Length > 0)
                return Translator.KeyboardShortcuts_InvalidShortcut;

            if (_shortcuts is null) return null;
            var candidate = new KeyboardShortcut { Mode = SelectedMode, Key = _key, ModifierKeys = _modifiers, Action = action };
            if (!_shortcuts.IsShortcutAllowed(candidate)) return Translator.KeyboardShortcuts_InvalidShortcut;
            if (await _shortcuts.IsKeyCombinationInUseAsync(SelectedMode, _key, _modifiers, _existingId)) return Translator.KeyboardShortcuts_ShortcutInUse;
            return null;
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Keyboard shortcut validation failed.");
            return Translator.KeyboardShortcuts_FailedToSave;
        }
    }

    private static KeyboardShortcutAction[] ActionsFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail =>
        [
            KeyboardShortcutAction.NewMail, KeyboardShortcutAction.ToggleReadUnread, KeyboardShortcutAction.ToggleFlag,
            KeyboardShortcutAction.ToggleArchive, KeyboardShortcutAction.Delete, KeyboardShortcutAction.Move,
            KeyboardShortcutAction.Reply, KeyboardShortcutAction.ReplyAll, KeyboardShortcutAction.Forward, KeyboardShortcutAction.Send,
        ],
        WinoApplicationMode.Calendar => [KeyboardShortcutAction.NewEvent, KeyboardShortcutAction.Delete],
        WinoApplicationMode.Contacts => [KeyboardShortcutAction.NewContact, KeyboardShortcutAction.Delete],
        WinoApplicationMode.Tasks => [KeyboardShortcutAction.NewTask, KeyboardShortcutAction.Delete],
        _ => [],
    };
}
