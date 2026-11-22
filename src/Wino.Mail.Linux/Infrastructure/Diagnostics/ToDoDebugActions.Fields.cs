#if DEBUG
using System.Globalization;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Task field verbs (MP-13): reminder latertoday|tomorrow|nextweek|none|yyyy-MM-ddTHH:mm, repeat
/// never|daily|weekdays|weekly|monthly|yearly|RRULE, priority High|Normal|Low, category add|remove name, link-open i,
/// field-menu reminder|repeat|priority|categories, field-dialog reminder|repeat|category, dialog-state, dialog-set
/// args (reminder: ISO; repeat: interval unit(0-3) MO,WE; category: text or pick name), dialog-confirm, dialog-cancel.
/// Each verb calls the member the drawer calls.
/// </summary>
internal static partial class ToDoDebugActions
{
    static partial void AddFieldVerbs()
    {
        Verb("reminder", args =>
        {
            var value = args.Length > 0 ? args[0] : "none";
            if (value is "latertoday" or "tomorrow" or "nextweek" or "none")
                return Page.ViewModel.SetReminderPresetCommand.ExecuteAsync(value).ContinueWith(_ => "done", TaskScheduler.Default);
            var local = DateTime.Parse(value, CultureInfo.InvariantCulture);
            return Run(task => Page.ViewModel.SetTaskReminderAsync(task, local));
        });
        Verb("repeat", args =>
        {
            var value = args.Length > 0 ? args[0] : "never";
            if (value is "never" or "daily" or "weekdays" or "weekly" or "monthly" or "yearly")
                return Page.ViewModel.SetRepeatPresetCommand.ExecuteAsync(value).ContinueWith(_ => "done", TaskScheduler.Default);
            return Run(task => Page.ViewModel.SetTaskRecurrenceAsync(task, value));
        });
        Verb("priority", args => Page.ViewModel.SetPriorityCommand.ExecuteAsync(args.Length > 0 ? args[0] : "Normal").ContinueWith(_ => "done", TaskScheduler.Default));
        Verb("category", args =>
        {
            if (args.Length < 2) throw new ArgumentException("category add|remove name");
            var name = string.Join(' ', args[1..]);
            return args[0] == "add"
                ? Run(task => Page.ViewModel.AddTaskCategoryAsync(task, name))
                : Page.ViewModel.RemoveTaskCategoryCommand.ExecuteAsync(name).ContinueWith(_ => "done", TaskScheduler.Default);
        });
        Verb("link-open", args =>
        {
            var index = int.Parse(args.Length > 0 ? args[0] : "0", CultureInfo.InvariantCulture);
            var item = Drawer.Links?.ItemAt(index) ?? throw new ArgumentException("no link " + index);
            _ = Page.ViewModel.OpenLinkedItemCommand.ExecuteAsync(item);
            return "opened " + item.AutomationName;
        });
        Verb("field-menu", args =>
        {
            Drawer.DebugShowFieldMenu(args.Length > 0 ? args[0] : "reminder");
            return ContextMenu.Current?.Describe() ?? "no menu";
        });
        Verb("field-dialog", async args =>
        {
            Drawer.DebugOpenFieldDialog(args.Length > 0 ? args[0] : "reminder");
            for (var attempt = 0; attempt < 20 && TaskFieldDialogs.Debug is null; attempt++) await Task.Delay(100);
            return TaskFieldDialogs.Debug?.Describe() ?? "no dialog";
        });
        Verb("dialog-state", _ => TaskFieldDialogs.Debug?.Describe() ?? "no dialog");
        Verb("dialog-set", args =>
        {
            var dialog = TaskFieldDialogs.Debug ?? throw new InvalidOperationException("no dialog");
            dialog.Set(args);
            return dialog.Describe();
        });
        Verb("dialog-confirm", _ =>
        {
            var dialog = TaskFieldDialogs.Debug ?? throw new InvalidOperationException("no dialog");
            dialog.Form.DebugConfirm();
            return "confirmed";
        });
        Verb("dialog-cancel", _ =>
        {
            var dialog = TaskFieldDialogs.Debug ?? throw new InvalidOperationException("no dialog");
            dialog.Form.Close(false);
            return "cancelled";
        });
    }
}
#endif
