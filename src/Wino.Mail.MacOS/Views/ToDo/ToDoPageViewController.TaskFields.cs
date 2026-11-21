using System;
using System.Threading.Tasks;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// The reminder, repeat, priority and category rows of the task drawer (Windows ToDoPage detail). The shared task
/// commands make each edit; the sheets only collect the value.
/// </summary>
public sealed partial class ToDoPageViewController
{
    private WinoDrawerActionRow _reminderRow = null!;
    private NSMenuItem _removeReminderItem = null!;
    private WinoDrawerActionRow _repeatRow = null!;
    private WinoDrawerActionRow _priorityRow = null!;
    private WinoDrawerActionRow _categoriesRow = null!;
    private NSMenu _categoriesMenu = null!;

    private void BuildTaskFieldRows()
    {
        var reminderMenu = new NSMenu { AutoEnablesItems = false };
        reminderMenu.AddItem(MenuItem(Translator.ToDoPage_ReminderPresetLaterToday, () => Observe(ViewModel.SetReminderPresetCommand.ExecuteAsync("latertoday"))));
        reminderMenu.AddItem(MenuItem(Translator.ToDoPage_ReminderPresetTomorrow, () => Observe(ViewModel.SetReminderPresetCommand.ExecuteAsync("tomorrow"))));
        reminderMenu.AddItem(MenuItem(Translator.ToDoPage_ReminderPresetNextWeek, () => Observe(ViewModel.SetReminderPresetCommand.ExecuteAsync("nextweek"))));
        reminderMenu.AddItem(MenuItem(Translator.ToDoPage_ReminderPickDateTime, () => Observe(PickReminderAsync())));
        reminderMenu.AddItem(NSMenuItem.SeparatorItem);
        reminderMenu.AddItem(_removeReminderItem = MenuItem(Translator.ToDoPage_RemoveReminder, () => Observe(ViewModel.SetReminderPresetCommand.ExecuteAsync("none"))));
        _reminderRow = new WinoDrawerActionRow(WinoIconGlyph.Alert, Translator.ToDoPage_RemindMe, WinoStyle.Accent) { Menu = reminderMenu, FollowsAccent = true };

        var repeatMenu = new NSMenu { AutoEnablesItems = false };
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatNever, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("never"))));
        repeatMenu.AddItem(NSMenuItem.SeparatorItem);
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetDaily, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("daily"))));
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetWeekdays, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("weekdays"))));
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetWeekly, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("weekly"))));
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetMonthly, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("monthly"))));
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetYearly, () => Observe(ViewModel.SetRepeatPresetCommand.ExecuteAsync("yearly"))));
        repeatMenu.AddItem(NSMenuItem.SeparatorItem);
        repeatMenu.AddItem(MenuItem(Translator.ToDoPage_RepeatPresetCustom, () => Observe(PickRepeatAsync())));
        _repeatRow = new WinoDrawerActionRow(WinoIconGlyph.CalendarEventRepeat, Translator.ToDoPage_Repeat, WinoStyle.Accent) { Menu = repeatMenu, FollowsAccent = true };

        var priorityMenu = new NSMenu { AutoEnablesItems = false };
        priorityMenu.AddItem(MenuItem(Translator.ToDoPage_PriorityHigh, () => Observe(ViewModel.SetPriorityCommand.ExecuteAsync("High"))));
        priorityMenu.AddItem(MenuItem(Translator.ToDoPage_PriorityNormal, () => Observe(ViewModel.SetPriorityCommand.ExecuteAsync("Normal"))));
        priorityMenu.AddItem(MenuItem(Translator.ToDoPage_PriorityLow, () => Observe(ViewModel.SetPriorityCommand.ExecuteAsync("Low"))));
        _priorityRow = new WinoDrawerActionRow(WinoIconGlyph.Flag, Translator.ToDoPage_Priority, WinoStyle.Accent) { Menu = priorityMenu, FollowsAccent = true };

        _categoriesMenu = new NSMenu { AutoEnablesItems = false };
        _categoriesRow = new WinoDrawerActionRow(WinoIconGlyph.Tag, Translator.ToDoPage_Categories, WinoStyle.Accent) { Menu = _categoriesMenu, FollowsAccent = true };
    }

    private void RebuildCategoriesMenu(TaskItemViewModel task)
    {
        _categoriesMenu.RemoveAllItems();
        foreach (var name in task.CategoryNames)
            _categoriesMenu.AddItem(MenuItem(string.Format(Translator.ToDoPage_RemoveCategory, name), () => Observe(ViewModel.RemoveTaskCategoryCommand.ExecuteAsync(name))));
        if (task.CategoryNames.Count > 0)
            _categoriesMenu.AddItem(NSMenuItem.SeparatorItem);
        _categoriesMenu.AddItem(MenuItem(Translator.ToDoPage_AddCategory, () => Observe(AddCategoryAsync(task))));
    }

    private void ApplyTaskFieldRows(TaskItemViewModel task)
    {
        _reminderRow.Text = task.ReminderDisplayText ?? string.Empty;
        _removeReminderItem.Enabled = task.HasReminder;
        _repeatRow.Text = task.RecurrenceDisplayText ?? string.Empty;
        _priorityRow.Trailing = task.PriorityDisplayText ?? string.Empty;
        _categoriesRow.Trailing = task.CategorySummaryText ?? string.Empty;
        RebuildCategoriesMenu(task);
    }

    private async Task PickReminderAsync()
    {
        var task = ViewModel.SelectedTask;
        if (task is null || View.Window is not { } window) return;

        var initial = task.ReminderAtUtc?.ToLocalTime() ?? DateTime.Now.AddHours(1);
        if (await TaskFieldSheets.PickReminderAsync(window, initial) is { } local)
            await ViewModel.SetTaskReminderAsync(task, local);
    }

    private async Task PickRepeatAsync()
    {
        var task = ViewModel.SelectedTask;
        if (task is null || View.Window is not { } window) return;

        var anchor = task.DueDate ?? DateTime.Now.Date;
        if (await TaskFieldSheets.PickRepeatAsync(window, task.Recurrence, anchor) is { } rule)
            await ViewModel.SetTaskRecurrenceAsync(task, rule);
    }

    private async Task AddCategoryAsync(TaskItemViewModel task)
    {
        if (View.Window is not { } window) return;

        var name = await TaskFieldSheets.PickCategoryAsync(window, ViewModel.GetCategorySuggestions(string.Empty));
        if (!string.IsNullOrEmpty(name))
            await ViewModel.AddTaskCategoryAsync(task, name);
    }
}
