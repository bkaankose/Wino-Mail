using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Global keys are application accelerators; Context keys are matched by <see cref="ShortcutRouter"/> because they depend on focus; SingleKey keys only act while <see cref="SingleKeyShortcuts"/> is on.</summary>
internal enum LinuxKeyScope { Global, Context, SingleKey }

internal enum LinuxKeyGroup { General, Navigation, Mail, Reader, Composer, Calendar, Tasks, SingleKey }

/// <summary>
/// One fixed key: an application action (<see cref="Action"/>) or, for a single key, a configurable action of the mode
/// (<see cref="Configurable"/>) run through the router's menu path.
/// </summary>
internal sealed record LinuxKey(string Action, string[] Accelerators, LinuxKeyScope Scope, LinuxKeyGroup Group, Func<string> Title,
    WinoApplicationMode? Mode = null, KeyboardShortcutAction? Configurable = null, bool WorksInTextInput = false, bool Repeats = false)
{
    public string? FirstAccelerator => Accelerators.Length > 0 ? Accelerators[0] : null;
}

/// <summary>
/// The fixed keys of the Linux head. Registration (<see cref="AppActions"/>), the router, the overview, tooltips, menu
/// hints and the recorder's conflict check all read this table, so a key is named in one place.
/// </summary>
internal static class LinuxKeymap
{
    public const string Preferences = AppActions.Preferences;
    public const string Shortcuts = AppActions.Shortcuts;
    public const string Quit = AppActions.Quit;
    public const string ToggleSidebar = AppActions.ToggleSidebar;
    public const string Synchronize = AppActions.Synchronize;
    public const string Back = AppActions.Back;
    public const string Search = AppActions.Search;
    public const string CloseWindow = "close-window";
    public const string NextPane = "focus-next-pane";
    public const string PreviousPane = "focus-previous-pane";
    public const string PaneBack = "pane-back";
    public const string NextItem = "next-item";
    public const string PreviousItem = "previous-item";
    public const string Junk = "mail-junk";
    public const string OpenInWindow = "open-in-window";
    public const string Print = "print";
    public const string ZoomIn = "zoom-in";
    public const string ZoomOut = "zoom-out";
    public const string ZoomReset = "zoom-reset";
    public const string ComposeAttach = "compose-attach";
    public const string CalendarToday = "calendar-today";
    public const string TaskComplete = "task-complete";
    public const string FocusSearch = "focus-search";

    private static readonly CalendarDisplayType[] CalendarViews = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];

    public static IReadOnlyList<LinuxKey> All { get; } = Build();

    /// <summary>The actions the main window's shell owns: they act only while the main window is the active window.</summary>
    public static bool IsShellAction(string action)
        => action is not (Preferences or Quit or CloseWindow or AppActions.About or AppActions.CheckForUpdates);

    public static LinuxKey? Find(string action) => All.FirstOrDefault(key => key.Action == action && key.Scope != LinuxKeyScope.SingleKey);

    public static string CalendarViewAction(int index) => "calendar-view-" + (index + 1);

    public static CalendarDisplayType? CalendarViewOf(string action)
        => action.StartsWith("calendar-view-", StringComparison.Ordinal) && int.TryParse(action.AsSpan(14), out var number) && number is >= 1 and <= 4
            ? CalendarViews[number - 1]
            : null;

    public static string ModeAction(WinoApplicationMode mode) => ShellModeActions.Name(mode);

    /// <summary>The first Global or Context key whose accelerator matches the gesture; null when none does.</summary>
    public static LinuxKey? Match(uint keyval, Gdk.ModifierType state, bool includeGlobal = false)
    {
        foreach (var key in All)
        {
            if (key.Scope == LinuxKeyScope.SingleKey || (key.Scope == LinuxKeyScope.Global && !includeGlobal)) continue;
            foreach (var accelerator in key.Accelerators)
            {
                if (Matches(accelerator, keyval, state)) return key;
            }
        }
        return null;
    }

    /// <summary>The single key for the character typed without Control, Alt or Super; matched on the character, because "#", "/" and "?" have no layout-independent key.</summary>
    public static LinuxKey? MatchSingle(uint keyval, Gdk.ModifierType state)
    {
        if ((state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) != 0) return null;
        var character = Gdk.Functions.KeyvalToUnicode(keyval);
        if (character == 0) return null;
        foreach (var key in All)
        {
            if (key.Scope == LinuxKeyScope.SingleKey && Gdk.Functions.KeyvalToUnicode(Gdk.Functions.KeyvalFromName(key.Accelerators[0])) == character) return key;
        }
        return null;
    }

    /// <summary>True when a fixed Global or Context key uses this accelerator (the shortcut recorder refuses it).</summary>
    public static bool Owns(string accelerator)
        => ShortcutKeys.TryParseAccelerator(accelerator, out var keyval, out var state) && Match(keyval, state, includeGlobal: true) is not null;

    /// <summary>The hint of a reader or list operation that has a fixed key (junk, print, pop out).</summary>
    public static string? AcceleratorFor(MailOperation operation) => operation switch
    {
        MailOperation.MoveToJunk or MailOperation.MarkAsNotJunk => Find(Junk)?.FirstAccelerator,
        MailOperation.Print => Find(Print)?.FirstAccelerator,
        _ => null
    };

    public static string? AcceleratorFor(string action) => Find(action)?.FirstAccelerator;

    /// <summary>"Synchronize (F5)" for a button tooltip.</summary>
    public static string Tooltip(string title, string action)
        => AcceleratorFor(action) is { } accelerator ? $"{title} ({WinoMenus.AcceleratorLabel(accelerator)})" : title;

    private static bool Matches(string accelerator, uint keyval, Gdk.ModifierType state)
    {
        if (!ShortcutKeys.TryParseAccelerator(accelerator, out var expected, out var modifiers)) return false;
        if (Gdk.Functions.KeyvalToLower(keyval) != Gdk.Functions.KeyvalToLower(expected)) return false;
        const Gdk.ModifierType Strict = Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask;
        state &= Gtk.Functions.AcceleratorGetDefaultModMask();
        if ((state & Strict) != (modifiers & Strict)) return false;
        // A symbol such as "plus" or "question" is typed with Shift on many layouts; Shift then belongs to the symbol.
        var character = Gdk.Functions.KeyvalToUnicode(expected);
        var shiftConsumed = character != 0 && !char.IsLetterOrDigit((char)character);
        return shiftConsumed || (state & Gdk.ModifierType.ShiftMask) == (modifiers & Gdk.ModifierType.ShiftMask);
    }

    private static List<LinuxKey> Build()
    {
        var keys = new List<LinuxKey>
        {
            new(Preferences, ["<Control>comma"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => Translator.MenuSettings),
            new(Shortcuts, ["F1", "<Control>question"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => Translator.Settings_KeyboardShortcuts_Title),
            new(Synchronize, ["F5"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => Translator.Buttons_Sync),
            new(Search, ["<Control>f"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => Translator.SearchBarPlaceholder),
            new(CloseWindow, ["<Control>w"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => Translator.MacOSMenu_CloseWindow),
            new(Quit, ["<Control>q"], LinuxKeyScope.Global, LinuxKeyGroup.General, static () => string.Format(Translator.MacOSMenu_Quit, AppActions.ApplicationName)),

            new(NextPane, ["F6"], LinuxKeyScope.Context, LinuxKeyGroup.Navigation, static () => Translator.Linux_Shortcuts_NextPane, WorksInTextInput: true),
            new(PreviousPane, ["<Shift>F6"], LinuxKeyScope.Context, LinuxKeyGroup.Navigation, static () => Translator.Linux_Shortcuts_PreviousPane, WorksInTextInput: true),
            new(ToggleSidebar, ["F9"], LinuxKeyScope.Global, LinuxKeyGroup.Navigation, static () => Translator.MacOSMenu_ToggleSidebar),
            new(PaneBack, ["Escape"], LinuxKeyScope.Context, LinuxKeyGroup.Navigation, static () => Translator.Linux_Shortcuts_PaneBack),
            new(NextItem, ["<Alt>Down"], LinuxKeyScope.Context, LinuxKeyGroup.Navigation, static () => Translator.Linux_Shortcuts_NextItem, Repeats: true),
            new(PreviousItem, ["<Alt>Up"], LinuxKeyScope.Context, LinuxKeyGroup.Navigation, static () => Translator.Linux_Shortcuts_PreviousItem, Repeats: true),
            new(Back, ["<Alt>Left"], LinuxKeyScope.Global, LinuxKeyGroup.Navigation, static () => Translator.Buttons_Back),
        };
        for (var index = 0; index < Wino.Core.Domain.Models.Accounts.AppModeVisibility.SwitchableModes.Length; index++)
        {
            var mode = Wino.Core.Domain.Models.Accounts.AppModeVisibility.SwitchableModes[index];
            keys.Add(new(ShellModeActions.Name(mode), [$"<Control>{index + 1}"], LinuxKeyScope.Global, LinuxKeyGroup.Navigation, () => Wino.Mail.ViewModels.Shell.ShellModeActivator.ModeTitle(mode)));
        }

        keys.Add(new(Junk, ["<Control>j"], LinuxKeyScope.Context, LinuxKeyGroup.Mail, static () => Translator.MailOperation_MarkAsJunk, WinoApplicationMode.Mail));
        keys.Add(new(OpenInWindow, ["<Control>o"], LinuxKeyScope.Context, LinuxKeyGroup.Mail, static () => Translator.Buttons_PopOut, WinoApplicationMode.Mail, WorksInTextInput: true));
        keys.Add(new(Print, ["<Control>p"], LinuxKeyScope.Context, LinuxKeyGroup.Reader, static () => Translator.MailOperation_Print, WinoApplicationMode.Mail));
        keys.Add(new(ZoomIn, ["<Control>plus", "<Control>equal", "<Control>KP_Add"], LinuxKeyScope.Context, LinuxKeyGroup.Reader, static () => Translator.Linux_Shortcuts_ZoomIn, WinoApplicationMode.Mail, Repeats: true));
        keys.Add(new(ZoomOut, ["<Control>minus", "<Control>KP_Subtract"], LinuxKeyScope.Context, LinuxKeyGroup.Reader, static () => Translator.Linux_Shortcuts_ZoomOut, WinoApplicationMode.Mail, Repeats: true));
        keys.Add(new(ZoomReset, ["<Control>0", "<Control>KP_0"], LinuxKeyScope.Context, LinuxKeyGroup.Reader, static () => Translator.Linux_Shortcuts_ZoomReset, WinoApplicationMode.Mail));
        keys.Add(new(ComposeAttach, ["<Control><Shift>a"], LinuxKeyScope.Context, LinuxKeyGroup.Composer, static () => Translator.Composer_AttachFiles, WinoApplicationMode.Mail, WorksInTextInput: true));

        keys.Add(new(CalendarToday, ["<Control>t"], LinuxKeyScope.Context, LinuxKeyGroup.Calendar, static () => Translator.Today, WinoApplicationMode.Calendar));
        for (var index = 0; index < CalendarViews.Length; index++)
        {
            var type = CalendarViews[index];
            keys.Add(new(CalendarViewAction(index), [$"<Alt>{index + 1}"], LinuxKeyScope.Context, LinuxKeyGroup.Calendar, () => Views.Calendar.CalendarToolbar.Title(type), WinoApplicationMode.Calendar));
        }
        keys.Add(new(TaskComplete, ["<Control>d"], LinuxKeyScope.Context, LinuxKeyGroup.Tasks, static () => Translator.DailyBriefing_ActionCompleteTask, WinoApplicationMode.Tasks));

        keys.Add(Single("j", NextItem, static () => Translator.Linux_Shortcuts_NextItem, repeats: true));
        keys.Add(Single("k", PreviousItem, static () => Translator.Linux_Shortcuts_PreviousItem, repeats: true));
        keys.Add(Single("r", KeyboardShortcutAction.Reply));
        keys.Add(Single("a", KeyboardShortcutAction.ReplyAll));
        keys.Add(Single("f", KeyboardShortcutAction.Forward));
        keys.Add(Single("e", KeyboardShortcutAction.ToggleArchive));
        keys.Add(Single("numbersign", KeyboardShortcutAction.Delete));
        keys.Add(Single("u", KeyboardShortcutAction.ToggleReadUnread));
        keys.Add(Single("s", KeyboardShortcutAction.ToggleFlag));
        keys.Add(Single("v", KeyboardShortcutAction.Move));
        keys.Add(Single("slash", FocusSearch, static () => Translator.SearchBarPlaceholder));
        keys.Add(Single("question", Shortcuts, static () => Translator.Settings_KeyboardShortcuts_Title));
        return keys;
    }

    private static LinuxKey Single(string keyName, string action, Func<string> title, bool repeats = false)
        => new(action, [keyName], LinuxKeyScope.SingleKey, LinuxKeyGroup.SingleKey, title, WinoApplicationMode.Mail, Repeats: repeats);

    private static LinuxKey Single(string keyName, KeyboardShortcutAction action)
        => new("single-" + action, [keyName], LinuxKeyScope.SingleKey, LinuxKeyGroup.SingleKey,
            () => Views.Dialogs.ShortcutsOverview.ActionTitle(WinoApplicationMode.Mail, action), WinoApplicationMode.Mail, action);
}
