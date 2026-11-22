#if DEBUG
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// MP-05: <c>debug-links</c> <c>as</c> <c>['reader-state', path]</c> (the reader's linked strip, one line per item
/// <c>glyph|title|subtitle|available|completed</c>), <c>['reader-open', index]</c> (opens an item as its button does);
/// <c>debug-task-dialog</c> <c>as</c> <c>['state', path]|['set', title, listIndex, dueIndex]|['confirm']|['cancel']</c> on the
/// open create-task dialog (-1 keeps a choice). Results also go to the log as <c>Debug action debug-links|debug-task-dialog …</c>.
/// </summary>
internal static class LinkedItemsDebugActions
{
    public static void Register(Adw.Application application)
    {
        Add(application, "debug-links", Links);
        Add(application, "debug-task-dialog", TaskDialog);
    }

    private static void Add(Adw.Application application, string name, Func<string[], string> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var arguments = args.Parameter?.GetStrv(out _) ?? [];
            string result;
            try { result = run(arguments); }
            catch (Exception exception)
            {
                result = "error: " + exception.Message;
                Serilog.Log.Error(exception, "Debug action {Name} failed.", name);
            }
            Serilog.Log.Information("Debug action {Name} {Verb}: {Result}", name, arguments.Length > 0 ? arguments[0] : string.Empty, result);
        };
        application.AddAction(action);
    }

    private static string Links(string[] arguments)
    {
        var reader = ReaderDiagnostics.Current ?? throw new InvalidOperationException("no reader");
        var strip = reader.Links ?? throw new InvalidOperationException("the reader has no linked strip");
        switch (arguments)
        {
            case ["reader-state", var path]:
                var text = $"visible={strip.Root.GetVisible()}\n{strip.Describe()}";
                DebugActions.WriteText(path, text);
                return text.Replace('\n', ';');
            case ["reader-open", var index]:
                var item = strip.ItemAt(int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)) ?? throw new ArgumentException("no item " + index);
                _ = reader.ViewModel.OpenLinkedItemCommand.ExecuteAsync(item);
                return "opened " + item.Title;
            default:
                return "verbs: reader-state path, reader-open index";
        }
    }

    private static string TaskDialog(string[] arguments)
    {
        var dialog = CreateTaskFromItemDialog.DebugCurrent ?? throw new InvalidOperationException("no create-task dialog");
        switch (arguments)
        {
            case ["state", var path]:
                var state = dialog.Describe();
                DebugActions.WriteText(path, state);
                return state;
            case ["set", var title, var list, var due]:
                dialog.DebugSet(title, int.Parse(list, System.Globalization.CultureInfo.InvariantCulture), int.Parse(due, System.Globalization.CultureInfo.InvariantCulture));
                return dialog.Describe();
            case ["confirm"]:
                dialog.DebugConfirm();
                return "confirmed";
            case ["cancel"]:
                dialog.DebugCancel();
                return "cancelled";
            default:
                return "verbs: state path, set title list due, confirm, cancel";
        }
    }
}
#endif
