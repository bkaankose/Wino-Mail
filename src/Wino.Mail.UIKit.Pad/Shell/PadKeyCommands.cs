using Foundation;
using ObjCRuntime;
using UIKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// The iPad hardware keyboard (the Mac main menu key equivalents and AppDelegate.Shortcuts). Built-in commands:
/// Cmd+1…4 modes, Cmd+N new item, Cmd+R reply, Shift+Cmd+R reply all, Shift+Cmd+F forward, Cmd+E archive, ⌫ trash,
/// Shift+Cmd+L flag, Shift+Cmd+U mark read, Cmd+F search and Cmd+, Settings. A user's Mail-mode shortcut with Command or
/// Control replaces the built-in key of its action (Settings › Keyboard shortcuts); other Mail-mode shortcuts and the
/// active mode's shortcuts in Calendar, Contacts and To Do are added and pass <see cref="KeyboardShortcutContextPolicy"/>
/// (no bare keys in text input, only Send while composing). Mail actions go to the content page's
/// <see cref="IShellCommandTarget"/>; mode shortcuts to the content page's ViewModel, then the mode's provider.
/// The list is rebuilt whenever UIKit asks, so commands that cannot run now are left out.
/// </summary>
internal sealed class PadKeyCommands
{
    public const string SelectorName = "padKeyCommand:";
    private static readonly Selector Action = new(SelectorName);

    private readonly PadShellViewController _shell;
    private readonly IKeyboardShortcutService? _shortcuts;
    private readonly Dictionary<string, Func<Task>> _handlers = new(StringComparer.Ordinal);
    private int _executing;

    public PadKeyCommands(PadShellViewController shell, IKeyboardShortcutService? shortcuts)
    {
        _shell = shell;
        _shortcuts = shortcuts;
    }

    private enum Builtin
    {
        ModeMail,
        ModeCalendar,
        ModeContacts,
        ModeTasks,
        NewItem,
        Search,
        Settings
    }

    /// <summary>The commands for the shell's current state.</summary>
    public UIKeyCommand[] Build()
    {
        _handlers.Clear();
        var commands = new List<UIKeyCommand>();
        var snapshot = _shortcuts?.EnabledShortcutsSnapshot ?? [];
        var responder = FirstResponder(_shell.View?.Window);
        bool textInput = IsTextInput(responder);

        Add(commands, "1", UIKeyModifierFlags.Command, UIKitShellViewController.ModeTitle(WinoApplicationMode.Mail), () => RunBuiltin(Builtin.ModeMail));
        Add(commands, "2", UIKeyModifierFlags.Command, UIKitShellViewController.ModeTitle(WinoApplicationMode.Calendar), () => RunBuiltin(Builtin.ModeCalendar));
        Add(commands, "3", UIKeyModifierFlags.Command, UIKitShellViewController.ModeTitle(WinoApplicationMode.Contacts), () => RunBuiltin(Builtin.ModeContacts));
        Add(commands, "4", UIKeyModifierFlags.Command, UIKitShellViewController.ModeTitle(WinoApplicationMode.Tasks), () => RunBuiltin(Builtin.ModeTasks));
        Add(commands, "f", UIKeyModifierFlags.Command, Translator.SearchBarPlaceholder, () => RunBuiltin(Builtin.Search));
        Add(commands, ",", UIKeyModifierFlags.Command, Translator.MenuSettings, () => RunBuiltin(Builtin.Settings));

        // Suggestion lists under the search field take the arrows and Esc while it edits.
        if (_shell.IsSearchEditing)
        {
            Add(commands, UIKeyCommand.UpArrow.ToString(), 0, string.Empty, () => SearchKey(ShellSearchFieldCommand.MoveUp));
            Add(commands, UIKeyCommand.DownArrow.ToString(), 0, string.Empty, () => SearchKey(ShellSearchFieldCommand.MoveDown));
            Add(commands, UIKeyCommand.Escape.ToString(), 0, string.Empty, () => SearchKey(ShellSearchFieldCommand.Cancel));
        }

        var mode = _shell.ActiveMode ?? WinoApplicationMode.Mail;
        if (mode == WinoApplicationMode.Mail)
        {
            AddMailCommands(commands, snapshot, responder, textInput);
            return commands.ToArray();
        }

        // File › New follows the mode: its "new" shortcut action, or Cmd+N when none is configured.
        if (NewActionFor(mode) is { } newAction && !snapshot.Any(item => item.Mode == mode && item.Action == newAction))
            Add(commands, "n", UIKeyModifierFlags.Command, NewTitleFor(mode), () => RouteAsync(mode, new KeyboardShortcutSnapshot(Guid.Empty, mode, string.Empty, ModifierKeys.None, newAction, DateTime.UtcNow)));
        AddModeShortcuts(commands, snapshot, mode, textInput);
        return commands.ToArray();
    }

    #region Mail

    private sealed record MailKey(KeyboardShortcutAction? Action, ShellCommand? Command, string Input, UIKeyModifierFlags Modifiers, string Title);

    /// <summary>The built-in Mail keys, each tied to the shortcut action that can replace it.</summary>
    private static MailKey[] MailDefaults() =>
    [
        new(KeyboardShortcutAction.NewMail, null, "n", UIKeyModifierFlags.Command, Translator.MenuNewMail),
        new(KeyboardShortcutAction.Reply, ShellCommand.Reply, "r", UIKeyModifierFlags.Command, Translator.MailOperation_Reply),
        new(KeyboardShortcutAction.ReplyAll, ShellCommand.ReplyAll, "r", UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, Translator.MailOperation_ReplyAll),
        new(null, ShellCommand.Forward, "f", UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, Translator.MailOperation_Forward),
        new(KeyboardShortcutAction.ToggleArchive, ShellCommand.Archive, "e", UIKeyModifierFlags.Command, Translator.MailOperation_Archive),
        new(KeyboardShortcutAction.Delete, ShellCommand.Delete, "\b", 0, Translator.MailOperation_Delete),
        new(KeyboardShortcutAction.ToggleFlag, ShellCommand.Flag, "l", UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, Translator.MailOperation_Flag),
        new(KeyboardShortcutAction.ToggleReadUnread, ShellCommand.ToggleRead, "u", UIKeyModifierFlags.Command | UIKeyModifierFlags.Shift, Translator.MarkReadUnread),
        new(KeyboardShortcutAction.Move, ShellCommand.Move, string.Empty, 0, Translator.MailOperation_Move)
    ];

    private void AddMailCommands(List<UIKeyCommand> commands, IReadOnlyList<KeyboardShortcutSnapshot> snapshot, UIResponder? responder, bool textInput)
    {
        var target = _shell.CommandTarget;
        // Text input inside the reader column is the composer: only Send runs there (Windows compose context).
        var context = textInput && _shell.IsInReaderFrame(responder) ? KeyboardShortcutInputContext.Compose : KeyboardShortcutInputContext.List;

        foreach (var key in MailDefaults())
        {
            if (key.Command is { } command && target?.CanExecute(command) != true) continue;

            var input = key.Input;
            var modifiers = key.Modifiers;
            var custom = key.Action is { } action
                ? snapshot.FirstOrDefault(item => item.Mode == WinoApplicationMode.Mail && item.Action == action && ShortcutKeys.HasCommandModifier(item.ModifierKeys) && ShortcutKeys.Input(item.Key) is not null)
                : null;
            if (custom is not null && ShortcutKeys.Input(custom.Key) is { } customInput)
            {
                input = customInput;
                modifiers = ShortcutKeys.Modifiers(custom.ModifierKeys);
            }
            if (input.Length == 0) continue;

            var shortcutAction = key.Action ?? KeyboardShortcutAction.None;
            var stored = custom?.Key ?? input;
            var storedModifiers = custom?.ModifierKeys ?? ToModifierKeys(modifiers);
            if (shortcutAction != KeyboardShortcutAction.None && !KeyboardShortcutContextPolicy.CanExecute(shortcutAction, stored, storedModifiers, context, textInput)) continue;
            // A bare key never fires while text is edited.
            if (textInput && modifiers == 0) continue;

            Add(commands, input, modifiers, key.Title, () => RunMail(key));
        }

        // Mail shortcuts without Command or Control (for example a bare letter) are extra keys for their action.
        foreach (var shortcut in snapshot)
        {
            if (shortcut.Mode != WinoApplicationMode.Mail || ShortcutKeys.HasCommandModifier(shortcut.ModifierKeys)) continue;
            if (ShortcutKeys.Input(shortcut.Key) is not { } input) continue;
            if (MailDefaults().FirstOrDefault(key => key.Action == shortcut.Action) is not { } key) continue;
            if (key.Command is { } command && target?.CanExecute(command) != true) continue;
            if (!KeyboardShortcutContextPolicy.CanExecute(shortcut.Action, shortcut.Key ?? string.Empty, shortcut.ModifierKeys, context, textInput)) continue;
            Add(commands, input, ShortcutKeys.Modifiers(shortcut.ModifierKeys), key.Title, () => RunMail(key));
        }
    }

    private Task RunMail(MailKey key)
    {
        if (key.Command is { } command)
        {
            if (_shell.CommandTarget is { } target && target.CanExecute(command)) target.Execute(command, null);
            return Task.CompletedTask;
        }
        _shell.NewItem();
        return Task.CompletedTask;
    }

    private static ModifierKeys ToModifierKeys(UIKeyModifierFlags flags)
    {
        var modifiers = ModifierKeys.None;
        if (flags.HasFlag(UIKeyModifierFlags.Command)) modifiers |= ModifierKeys.Command;
        if (flags.HasFlag(UIKeyModifierFlags.Control)) modifiers |= ModifierKeys.Control;
        if (flags.HasFlag(UIKeyModifierFlags.Alternate)) modifiers |= ModifierKeys.Alt;
        if (flags.HasFlag(UIKeyModifierFlags.Shift)) modifiers |= ModifierKeys.Shift;
        return modifiers;
    }

    #endregion

    #region Calendar, Contacts, To Do (Windows KeyboardShortcutController)

    private void AddModeShortcuts(List<UIKeyCommand> commands, IReadOnlyList<KeyboardShortcutSnapshot> snapshot, WinoApplicationMode mode, bool textInput)
    {
        if (ShortcutContextFor(mode) is not { } context || !IsEligibleRoot(mode, _shell.ContentViewModel)) return;

        foreach (var shortcut in snapshot)
        {
            if (shortcut.Mode != mode || ShortcutKeys.Input(shortcut.Key) is not { } input) continue;
            if (!KeyboardShortcutContextPolicy.CanExecute(shortcut.Action, shortcut.Key ?? string.Empty, shortcut.ModifierKeys, context, textInput)) continue;
            var captured = shortcut;
            Add(commands, input, ShortcutKeys.Modifiers(shortcut.ModifierKeys), shortcut.Action.ToString(), () => RouteAsync(mode, captured));
        }
    }

    /// <summary>The content page's ViewModel handles the shortcut first, then the mode's provider.</summary>
    private async Task RouteAsync(WinoApplicationMode mode, KeyboardShortcutSnapshot shortcut)
    {
        if (!IsEligibleRoot(mode, _shell.ContentViewModel)) return;
        await _shell.RouteKeyboardShortcutAsync(new KeyboardShortcutTriggerDetails
        {
            ShortcutId = shortcut.Id,
            Mode = shortcut.Mode,
            Action = shortcut.Action,
            Key = shortcut.Key ?? string.Empty,
            ModifierKeys = shortcut.ModifierKeys,
            InputContext = ShortcutContextFor(mode) ?? KeyboardShortcutInputContext.List,
            Sender = _shell,
            Origin = (object?)FirstResponder(_shell.View?.Window) ?? _shell
        });
    }

    private static KeyboardShortcutInputContext? ShortcutContextFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => KeyboardShortcutInputContext.Calendar,
        WinoApplicationMode.Contacts => KeyboardShortcutInputContext.Contacts,
        WinoApplicationMode.Tasks => KeyboardShortcutInputContext.Tasks,
        _ => null
    };

    /// <summary>Windows IsEligibleRootSurface: People and To Do only route while their page is the content.</summary>
    private static bool IsEligibleRoot(WinoApplicationMode mode, object? contentViewModel) => mode switch
    {
        WinoApplicationMode.Contacts => contentViewModel is ContactsPageViewModel,
        WinoApplicationMode.Tasks => contentViewModel is ToDoPageViewModel,
        _ => true
    };

    private static KeyboardShortcutAction? NewActionFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => KeyboardShortcutAction.NewEvent,
        WinoApplicationMode.Contacts => KeyboardShortcutAction.NewContact,
        WinoApplicationMode.Tasks => KeyboardShortcutAction.NewTask,
        _ => null
    };

    private static string NewTitleFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => Translator.CalendarEventCompose_NewEventButton,
        WinoApplicationMode.Contacts => Translator.KeyboardShortcuts_ActionNewContact,
        WinoApplicationMode.Tasks => Translator.KeyboardShortcuts_ActionNewTask,
        _ => Translator.MenuNewMail
    };

    #endregion

    private Task SearchKey(ShellSearchFieldCommand command)
    {
        if (!_shell.SearchFieldCommand(command) && command == ShellSearchFieldCommand.Cancel) _shell.EndSearchEditing();
        return Task.CompletedTask;
    }

    private Task RunBuiltin(Builtin builtin)
    {
        switch (builtin)
        {
            case Builtin.ModeMail: _shell.RequestMode(WinoApplicationMode.Mail); break;
            case Builtin.ModeCalendar: _shell.RequestMode(WinoApplicationMode.Calendar); break;
            case Builtin.ModeContacts: _shell.RequestMode(WinoApplicationMode.Contacts); break;
            case Builtin.ModeTasks: _shell.RequestMode(WinoApplicationMode.Tasks); break;
            case Builtin.NewItem: _shell.NewItem(); break;
            case Builtin.Search: _shell.FocusSearch(); break;
            case Builtin.Settings: _shell.OpenSettings(); break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Adds a command once per input and modifiers (the first registration wins, like a menu key equivalent).</summary>
    private void Add(List<UIKeyCommand> commands, string input, UIKeyModifierFlags modifiers, string title, Func<Task> handler)
    {
        var id = $"{(long)modifiers}:{input}";
        if (_handlers.ContainsKey(id)) return;
        _handlers[id] = handler;
        var command = UIKeyCommand.Create(title, null, Action, input, modifiers, new NSString(id));
        command.WantsPriorityOverSystemBehavior = true;
        commands.Add(command);
    }

    /// <summary>Runs the command the shell received; a held key repeats, but a command runs once at a time.</summary>
    public async void Invoke(UIKeyCommand command)
    {
        if (command.PropertyList is not NSString id || !_handlers.TryGetValue(id.ToString(), out var handler)) return;
        if (Interlocked.Exchange(ref _executing, 1) != 0) return;
        try { await handler(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "A keyboard command failed."); }
        finally { Volatile.Write(ref _executing, 0); }
    }

    /// <summary>The window's first responder, found by walking its views (UIKit has no public accessor).</summary>
    public static UIResponder? FirstResponder(UIView? root)
    {
        if (root is null) return null;
        if (root.IsFirstResponder) return root;
        foreach (var child in root.Subviews)
            if (FirstResponder(child) is { } found) return found;
        return null;
    }

    /// <summary>Text input: a text field or text view, or anything inside a web view (the composer's editor).</summary>
    public static bool IsTextInput(UIResponder? responder)
    {
        if (responder is UITextField or UITextView) return true;
        for (var view = responder as UIView; view is not null; view = view.Superview)
            if (view is WKWebView) return true;
        return false;
    }
}
