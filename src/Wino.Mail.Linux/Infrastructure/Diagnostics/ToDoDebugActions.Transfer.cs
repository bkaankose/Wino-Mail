#if DEBUG
namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Undo, notice and list transfer verbs (MP-12): bars, undo, close-undo, close-notice, copy-list, export ics|csv,
/// import. Export and import paths come from <c>FilePickers.DebugNext</c> (<c>debug-accounts ['pick', path]</c>); the
/// list pickers answer through <c>debug-picker-choose</c>.
/// </summary>
internal static partial class ToDoDebugActions
{
    static partial void AddTransferVerbs()
    {
        Verb("bars", _ => Page.DebugDescribeBars());
        Verb("add-state", _ =>
        {
            var vm = Page.ViewModel;
            return $"canCreate={vm.CanCreateTask} running={vm.AddTaskCommand.IsRunning} canExecute={vm.AddTaskCommand.CanExecute(null)} composer='{vm.ComposerText}'";
        });
        Verb("undo", _ =>
        {
            var page = Page;
            page.DebugInvokeUndo();
            return page.DebugDescribeBars();
        });
        Verb("close-undo", _ =>
        {
            var page = Page;
            page.DebugCloseBar(true);
            return page.DebugDescribeBars();
        });
        Verb("close-notice", _ =>
        {
            var page = Page;
            page.DebugCloseBar(false);
            return page.DebugDescribeBars();
        });
        Verb("copy-list", args =>
        {
            _ = Page.ViewModel.CopySelectedListCommand.ExecuteAsync(null);
            return "started";
        });
        Verb("export", args =>
        {
            _ = Page.ViewModel.ExportTasksCommand.ExecuteAsync(args.Length > 0 ? args[0] : "ics");
            return "started";
        });
        Verb("import", args =>
        {
            _ = Page.ViewModel.ImportTasksCommand.ExecuteAsync(null);
            return "started";
        });
    }
}
#endif
