#if DEBUG
using System.Globalization;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Drawer verbs (TD-04): drawer-state, drawer-title t, drawer-notes t, drawer-due today|tomorrow|nextweek|none,
/// drawer-due-menu, drawer-myday, drawer-important, drawer-complete, drawer-event, drawer-close, drawer-back,
/// step-add t, step-toggle i, step-rename i t, step-delete i, bulk complete|reopen|important|delete.
/// </summary>
internal static partial class ToDoDebugActions
{
    private static ToDoDetailDrawer Drawer => Page.Drawer ?? throw new InvalidOperationException("no drawer");

    static partial void AddDrawerVerbs()
    {
        Verb("drawer-state", _ => Drawer.Describe());
        Verb("drawer-title", args =>
        {
            Drawer.DebugSetTitle(string.Join(' ', args));
            return Drawer.Describe();
        });
        Verb("drawer-notes", args =>
        {
            Drawer.DebugSetNotes(string.Join(' ', args).Replace("\\n", "\n", StringComparison.Ordinal));
            return Drawer.Describe();
        });
        Verb("drawer-due", async args =>
        {
            var page = Page;
            await page.ViewModel.SetDuePresetCommand.ExecuteAsync(args.Length > 0 ? args[0] : "none");
            return Drawer.Describe();
        });
        Verb("drawer-due-menu", _ =>
        {
            Drawer.DebugShowDueMenu();
            return ContextMenu.Current?.Describe() ?? "no menu";
        });
        Verb("drawer-myday", _ => Run(task => Page.ViewModel.ToggleMyDayCommand.ExecuteAsync(task)));
        Verb("drawer-important", _ => Run(task => Page.ViewModel.ToggleImportanceCommand.ExecuteAsync(task)));
        Verb("drawer-complete", _ => Run(task => Page.ViewModel.ToggleTaskCommand.ExecuteAsync(task)));
        Verb("drawer-event", async _ =>
        {
            await Page.ViewModel.CreateCalendarEventCommand.ExecuteAsync(null);
            return "event requested";
        });
        Verb("drawer-close", _ =>
        {
            Page.ViewModel.CloseDetailCommand.Execute(null);
            return Drawer.Describe();
        });
        Verb("drawer-back", _ =>
        {
            Page.ViewModel.BackToTaskListCommand.Execute(null);
            return Drawer.Describe();
        });
        Verb("step-add", args =>
        {
            Drawer.DebugAddStep(string.Join(' ', args));
            return "added";
        });
        Verb("step-toggle", args =>
        {
            var row = StepRow(args);
            row.Check.Button.Activate();
            return "toggled";
        });
        Verb("step-rename", args =>
        {
            var row = StepRow(args);
            row.Entry.GrabFocus();
            row.Entry.SetText(string.Join(' ', args.Skip(1)));
            row.Commit();
            return "renamed";
        });
        Verb("step-delete", args =>
        {
            StepRow(args).DeleteButton.Activate();
            return "delete requested";
        });
        Verb("bulk", args =>
        {
            var bar = Drawer.Multi;
            var index = (args.Length > 0 ? args[0] : string.Empty) switch
            {
                "complete" => 0,
                "reopen" => 1,
                "important" => 2,
                "delete" => 3,
                _ => throw new ArgumentException("bulk complete|reopen|important|delete"),
            };
            var button = bar.Actions[index];
            if (!button.GetSensitive()) return "insensitive";
            button.Activate();
            return "ran " + args[0];
        });
    }

    private static Task<string> Run(Func<TaskItemViewModel, Task> action)
        => Drawer.Task is { } task ? action(task).ContinueWith(_ => "done", TaskScheduler.Default) : Task.FromResult("no task");

    private static WinoStepRow StepRow(string[] args)
    {
        var index = int.Parse(args.Length > 0 ? args[0] : "0", CultureInfo.InvariantCulture);
        return Drawer.StepRow(index) ?? throw new ArgumentException("no step " + index);
    }
}
#endif
