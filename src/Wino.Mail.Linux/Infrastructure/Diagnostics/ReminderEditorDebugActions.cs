#if DEBUG
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// MP-11: <c>debug-reminder-editor</c> <c>as</c> on the open reminders editor: <c>['state', path]</c> (one line per row
/// <c>value|unit|direction|method|summary</c>), <c>['add']</c>, <c>['remove', i]</c>, <c>['set', i, value, unitIndex, directionIndex(, methodIndex)]</c>
/// (through the row's spin button and drop-downs), <c>['save']</c>, <c>['cancel']</c>. Results also go to the log.
/// </summary>
internal static class ReminderEditorDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-reminder-editor", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var arguments = args.Parameter?.GetStrv(out _) ?? [];
            string result;
            try { result = Run(arguments); }
            catch (Exception exception)
            {
                result = "error: " + exception.Message;
                Serilog.Log.Error(exception, "Debug action debug-reminder-editor failed.");
            }
            Serilog.Log.Information("Debug action debug-reminder-editor {Verb}: {Result}", arguments.Length > 0 ? arguments[0] : string.Empty, result);
        };
        application.AddAction(action);
    }

    private static string Run(string[] arguments)
    {
        if (arguments is ["state", var path])
        {
            var text = CalendarReminderEditorDialog.DebugDescribe() ?? "no editor";
            DebugActions.WriteText(path, text);
            return text.Replace('\n', ';');
        }
        if (arguments.Length == 0) return "usage: state p | add | remove i | set i v u d [m] | save | cancel";
        return CalendarReminderEditorDialog.DebugApply(arguments[0], arguments[1..]) ? "ok" : "no editor or bad arguments";
    }
}
#endif
