using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.Linux.Views.Links;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The reminder, repeat, priority and category rows of the task drawer and the items linked to the task (macOS
/// ToDoPageViewController.TaskFields and .Detail, Windows ToDoPage detail). The shared task commands make each edit;
/// the forms in <see cref="TaskFieldDialogs"/> only collect the value.
/// </summary>
internal sealed partial class ToDoDetailDrawer
{
    private readonly Gtk.Box _linksSection = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private LinkedItemStrip? _links;

    private Gtk.Widget BuildLinks()
    {
        var observe = _observe;
        var viewModel = _viewModel;
        var strip = _links = new LinkedItemStrip(item => observe(viewModel.OpenLinkedItemCommand.ExecuteAsync(item)), Gtk.Orientation.Vertical);
        _linksSection.Append(WinoDrawerSection.Label(Translator.ConnectedItems_LinkedHeader));
        _linksSection.Append(Indent(strip.Root, 12));
        _linksSection.SetVisible(false);
        strip.Attach(viewModel.SelectedTaskLinks);
        return _linksSection;
    }

    private void ConnectFieldRows()
    {
        _remind.Clicked += ShowReminderMenu;
        _repeat.Clicked += ShowRepeatMenu;
        _priority.Clicked += ShowPriorityMenu;
        _categories.Clicked += ShowCategoriesMenu;
    }

    private void ApplyFieldRows(TaskItemViewModel task, bool editable)
    {
        _remind.Text = task.ReminderDisplayText ?? Translator.ToDoPage_RemindMe;
        _remind.Accent = task.HasReminder;
        _remind.Sensitive = editable;
        _repeat.Text = task.RecurrenceDisplayText ?? Translator.ToDoPage_Repeat;
        _repeat.Accent = task.IsRecurring;
        _repeat.Sensitive = editable;
        _priority.Trailing = task.PriorityDisplayText;
        _priority.Sensitive = editable;
        var categories = task.CategorySummaryText;
        _categories.Trailing = string.IsNullOrEmpty(categories) ? null : categories;
        _categories.Sensitive = editable;
        _linksSection.SetVisible(_viewModel.HasSelectedTaskLinks);
    }

    private void ShowReminderMenu()
    {
        if (_disposed || _task is not { IsEditable: true } task) return;
        var menu = WinoMenus.Create();
        var viewModel = _viewModel;
        var observe = _observe;
        menu.Item(Translator.ToDoPage_ReminderPresetLaterToday, () => observe(viewModel.SetReminderPresetCommand.ExecuteAsync("latertoday")));
        menu.Item(Translator.ToDoPage_ReminderPresetTomorrow, () => observe(viewModel.SetReminderPresetCommand.ExecuteAsync("tomorrow")));
        menu.Item(Translator.ToDoPage_ReminderPresetNextWeek, () => observe(viewModel.SetReminderPresetCommand.ExecuteAsync("nextweek")));
        menu.Item(Translator.ToDoPage_ReminderPickDateTime, PickReminder);
        menu.Separator();
        menu.Item(Translator.ToDoPage_RemoveReminder, () => observe(viewModel.SetReminderPresetCommand.ExecuteAsync("none")), task.HasReminder);
        ContextMenu.Show(_remind.Root, menu.Build());
    }

    private void ShowRepeatMenu()
    {
        if (_disposed || _task is not { IsEditable: true }) return;
        var menu = WinoMenus.Create();
        var viewModel = _viewModel;
        var observe = _observe;
        void Preset(string label, string preset) => menu.Item(label, () => observe(viewModel.SetRepeatPresetCommand.ExecuteAsync(preset)));
        Preset(Translator.ToDoPage_RepeatNever, "never");
        menu.Separator();
        Preset(Translator.ToDoPage_RepeatPresetDaily, "daily");
        Preset(Translator.ToDoPage_RepeatPresetWeekdays, "weekdays");
        Preset(Translator.ToDoPage_RepeatPresetWeekly, "weekly");
        Preset(Translator.ToDoPage_RepeatPresetMonthly, "monthly");
        Preset(Translator.ToDoPage_RepeatPresetYearly, "yearly");
        menu.Separator();
        menu.Item(Translator.ToDoPage_RepeatPresetCustom, PickRepeat);
        ContextMenu.Show(_repeat.Root, menu.Build());
    }

    private void ShowPriorityMenu()
    {
        if (_disposed || _task is not { IsEditable: true }) return;
        var menu = WinoMenus.Create();
        var viewModel = _viewModel;
        var observe = _observe;
        menu.Item(Translator.ToDoPage_PriorityHigh, () => observe(viewModel.SetPriorityCommand.ExecuteAsync("High")));
        menu.Item(Translator.ToDoPage_PriorityNormal, () => observe(viewModel.SetPriorityCommand.ExecuteAsync("Normal")));
        menu.Item(Translator.ToDoPage_PriorityLow, () => observe(viewModel.SetPriorityCommand.ExecuteAsync("Low")));
        ContextMenu.Show(_priority.Root, menu.Build());
    }

    private void ShowCategoriesMenu()
    {
        if (_disposed || _task is not { IsEditable: true } task) return;
        var menu = WinoMenus.Create();
        var viewModel = _viewModel;
        var observe = _observe;
        var names = task.CategoryNames;
        foreach (var name in names)
            menu.Item(string.Format(Translator.ToDoPage_RemoveCategory, name), () => observe(viewModel.RemoveTaskCategoryCommand.ExecuteAsync(name)));
        if (names.Count > 0) menu.Separator();
        menu.Item(Translator.ToDoPage_AddCategory, AddCategory);
        ContextMenu.Show(_categories.Root, menu.Build());
    }

    private void PickReminder()
    {
        if (_disposed || _task is not { } task || _stack.GetRoot() is not Gtk.Widget owner) return;
        _observe(PickReminderAsync(task, owner));
    }

    private async Task PickReminderAsync(TaskItemViewModel task, Gtk.Widget owner)
    {
        var initial = task.ReminderAtUtc?.ToLocalTime() ?? DateTime.Now.AddHours(1);
        if (await TaskFieldDialogs.PickReminderAsync(owner, initial, null) is { } local && IsStillSelected(task))
            await _viewModel.SetTaskReminderAsync(task, local);
    }

    private void PickRepeat()
    {
        if (_disposed || _task is not { } task || _stack.GetRoot() is not Gtk.Widget owner) return;
        _observe(PickRepeatAsync(task, owner));
    }

    private async Task PickRepeatAsync(TaskItemViewModel task, Gtk.Widget owner)
    {
        var anchor = task.DueDate ?? DateTime.Now.Date;
        if (await TaskFieldDialogs.PickRepeatAsync(owner, task.Recurrence, anchor) is { } rule && IsStillSelected(task))
            await _viewModel.SetTaskRecurrenceAsync(task, rule);
    }

    private void AddCategory()
    {
        if (_disposed || _task is not { } task || _stack.GetRoot() is not Gtk.Widget owner) return;
        _observe(AddCategoryAsync(task, owner));
    }

    private async Task AddCategoryAsync(TaskItemViewModel task, Gtk.Widget owner)
    {
        var name = await TaskFieldDialogs.PickCategoryAsync(owner, _viewModel.GetCategorySuggestions(string.Empty));
        if (!string.IsNullOrEmpty(name) && IsStillSelected(task))
            await _viewModel.AddTaskCategoryAsync(task, name);
    }

    private bool IsStillSelected(TaskItemViewModel task) => !_disposed && ReferenceEquals(_viewModel.SelectedTask, task);

    private void DisposeFields()
    {
        _links?.Detach();
        _links?.Dispose();
        _links = null;
    }

#if DEBUG
    internal LinkedItemStrip? Links => _links;

    internal void DebugShowFieldMenu(string row)
    {
        switch (row)
        {
            case "reminder": ShowReminderMenu(); break;
            case "repeat": ShowRepeatMenu(); break;
            case "priority": ShowPriorityMenu(); break;
            case "categories": ShowCategoriesMenu(); break;
            default: throw new ArgumentException("reminder|repeat|priority|categories");
        }
    }

    internal void DebugOpenFieldDialog(string kind)
    {
        switch (kind)
        {
            case "reminder": PickReminder(); break;
            case "repeat": PickRepeat(); break;
            case "category": AddCategory(); break;
            default: throw new ArgumentException("reminder|repeat|category");
        }
    }
#endif
}
