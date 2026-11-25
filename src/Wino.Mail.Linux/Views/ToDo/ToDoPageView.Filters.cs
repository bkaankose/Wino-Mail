using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The filter row (Windows ToDoPage filter grid, macOS BuildFilterRow): filter entry, Show scope, Important, Sort and
/// Clear, collapsed into one Filter chip while the layout is compact; the task context menu with its date picker.
/// </summary>
internal sealed partial class ToDoPageView
{
    private static readonly (TaskSortKind Kind, string Parameter)[] SortKinds =
    [
        (TaskSortKind.Importance, "importance"),
        (TaskSortKind.DueDate, "due"),
        (TaskSortKind.MyDay, "myday"),
        (TaskSortKind.Alphabetical, "alphabetical"),
        (TaskSortKind.CreationDate, "created"),
        (TaskSortKind.Manual, "manual"),
    ];

    private readonly Dictionary<TaskCell, ContextMenuAttachment> _taskMenus = [];
    private Gtk.SearchEntry? _filter;
    private Gtk.Box? _expandedFilters;
    private WinoChipButton? _scopeChip;
    private WinoChipButton? _importantChip;
    private WinoChipButton? _sortChip;
    private WinoChipButton? _clearChip;
    private WinoChipButton? _compactChip;
    private WinoDatePickerPopover? _datePicker;
    private bool _applyingFilter;

    internal Gtk.SearchEntry? FilterEntry => _filter;

    internal bool IsFilterCompact => _compactChip?.Visible == true;

    internal string FilterChipText => $"scope='{_scopeChip?.Title}' important={_importantChip?.Checked} sort='{_sortChip?.Title}' clear={_clearChip?.Enabled}";

    internal WinoDatePickerPopover? DatePicker => _datePicker;

    partial void BuildFilters(ToDoListHeader header)
    {
        var self = _self;
        var filter = _filter = Gtk.SearchEntry.New();
        filter.SetPlaceholderText(Translator.ToDoPage_FilterTasks);
        filter.SetSearchDelay(250);
        filter.SetHexpand(true);
        filter.SetSizeRequest(160, -1);
        WinoA11y.Label(filter, Translator.ToDoPage_FilterTasks);
        Bindings.Own(GtkSignals.Connect<Gtk.SearchEntry>(h => filter.OnSearchChanged += h, h => filter.OnSearchChanged -= h, (_, _) =>
        {
            if (self.Value is { _applyingFilter: false } view) view.ViewModel.FilterText = filter.GetText();
        }));
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(360);
        clamp.SetTighteningThreshold(160);
        clamp.SetHalign(Gtk.Align.Start);
        clamp.SetHexpand(true);
        clamp.SetChild(filter);

        _scopeChip = Bindings.Own(new WinoChipButton(WinoIconGlyph.List, null, Translator.ToDoPage_ShowScope, hasMenu: true));
        _scopeChip.Clicked += () => self.Value?.ShowFilterMenu(self.Value._scopeChip, ScopeItems);
        _importantChip = Bindings.Own(new WinoChipButton(WinoIconGlyph.Star, Translator.ToDoPage_Important, Translator.ToDoPage_Important));
        _importantChip.Clicked += () => self.Value?.ToggleImportantFilter();
        _sortChip = Bindings.Own(new WinoChipButton(WinoIconGlyph.ArrowSort, null, Translator.ToDoPage_SortBy, hasMenu: true));
        _sortChip.Clicked += () => self.Value?.ShowFilterMenu(self.Value._sortChip, SortItems);
        _clearChip = Bindings.Own(new WinoChipButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_Clear, Translator.ToDoPage_ClearFilters));
        _clearChip.Clicked += () => self.Value?.ViewModel.ClearFiltersCommand.Execute(null);
        _compactChip = Bindings.Own(new WinoChipButton(WinoIconGlyph.Filter, null, Translator.ToDoPage_FilterOptions, hasMenu: true));
        _compactChip.Clicked += () => self.Value?.ShowFilterMenu(self.Value._compactChip, CompactItems);
        _compactChip.Visible = false;

        _expandedFilters = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _expandedFilters.Append(_scopeChip.Root);
        _expandedFilters.Append(_importantChip.Root);
        _expandedFilters.Append(_sortChip.Root);
        _expandedFilters.Append(_clearChip.Root);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.Append(clamp);
        row.Append(_expandedFilters);
        row.Append(_compactChip.Root);
        // The chips keep their natural width; an external-policy scroller keeps them from raising the page's minimum width
        // between the Wide breakpoint and the width they need, where they clip instead.
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        scroller.SetPropagateNaturalHeight(true);
        scroller.SetPropagateNaturalWidth(true);
        scroller.SetChild(row);
        header.SetFilter(scroller);
        header.SuggestionsRequested += () => self.Value?.ShowSuggestions();
    }

    partial void BindFilters()
    {
        Bind.Bind(nameof(ToDoPageViewModel.FilterText), static vm => vm.FilterText, value =>
        {
            if (_filter is null || string.Equals(_filter.GetText(), value ?? string.Empty, StringComparison.Ordinal)) return;
            _applyingFilter = true;
            try { _filter.SetText(value ?? string.Empty); }
            finally { _applyingFilter = false; }
        });
        Bind.Bind(nameof(ToDoPageViewModel.CompletionScopeDisplayText), static vm => vm.CompletionScopeDisplayText, value =>
        {
            if (_scopeChip is not null) _scopeChip.Title = value;
        });
        Bind.Bind(nameof(ToDoPageViewModel.SortDisplayText), static vm => vm.SortDisplayText, value =>
        {
            if (_sortChip is not null) _sortChip.Title = value;
        });
        Bind.Bind(nameof(ToDoPageViewModel.IsImportantTasksFilterSelected), static vm => vm.IsImportantTasksFilterSelected, value =>
        {
            if (_importantChip is not null) _importantChip.Checked = value;
        });
        Bind.Bind(nameof(ToDoPageViewModel.IsFiltered), static vm => vm.IsFiltered, value =>
        {
            if (_clearChip is not null) _clearChip.Enabled = value;
        });
        Bind.Bind(nameof(ToDoPageViewModel.IsCompactLayout), static vm => vm.IsCompactLayout, compact =>
        {
            _expandedFilters?.SetVisible(!compact);
            if (_compactChip is not null) _compactChip.Visible = compact;
        });
        Bind.Bind(nameof(ToDoPageViewModel.IsMyDaySelected), static vm => vm.IsMyDaySelected, _header.SetSuggestionsVisible);
        BindSuggestions();
        Bindings.Own(new ActionDisposable(() =>
        {
            foreach (var menu in _taskMenus.Values) menu.Dispose();
            _taskMenus.Clear();
            CloseDatePicker();
        }));
    }

    private void ShowFilterMenu(WinoChipButton? chip, Action<ToDoPageView, WinoMenuBuilder> populate)
    {
        if (IsReleased || chip is null) return;
        var menu = WinoMenus.Create();
        populate(this, menu);
        ContextMenu.Show(chip.Root, menu.Build());
    }

    internal static void ScopeItems(ToDoPageView view, WinoMenuBuilder menu)
    {
        var vm = view.ViewModel;
        var self = view._self;
        void Scope(string label, TaskCompletionScope scope, string parameter)
            => menu.Radio("scope", label, vm.SelectedCompletionScope == scope, () => self.Value?.ViewModel.SetCompletionScopeCommand.Execute(parameter));
        Scope(Translator.ToDoPage_ScopeActive, TaskCompletionScope.Active, "active");
        Scope(Translator.ToDoPage_Completed, TaskCompletionScope.Completed, "completed");
        Scope(Translator.ToDoPage_All, TaskCompletionScope.All, "all");
    }

    internal static void SortItems(ToDoPageView view, WinoMenuBuilder menu)
    {
        var vm = view.ViewModel;
        var self = view._self;
        foreach (var (kind, parameter) in SortKinds)
            menu.Radio("sort", SortTitle(kind), vm.SelectedSort == kind, () => self.Value?.ViewModel.SetSortCommand.Execute(parameter));
    }

    internal static void CompactItems(ToDoPageView view, WinoMenuBuilder menu)
    {
        var vm = view.ViewModel;
        var self = view._self;
        menu.Submenu(Translator.ToDoPage_ShowScope, scope => ScopeItems(view, scope), icon: WinoIconGlyph.List);
        menu.Check(Translator.ToDoPage_Important, vm.IsImportantTasksFilterSelected, _ => self.Value?.ToggleImportantFilter(), icon: WinoIconGlyph.Star);
        menu.Submenu(Translator.ToDoPage_SortBy, sort => SortItems(view, sort), icon: WinoIconGlyph.ArrowSort);
        menu.Separator();
        menu.Item(Translator.ToDoPage_ClearFilters, () => self.Value?.ViewModel.ClearFiltersCommand.Execute(null), vm.IsFiltered, icon: WinoIconGlyph.Dismiss);
    }

    private static string SortTitle(TaskSortKind kind) => kind switch
    {
        TaskSortKind.Importance => Translator.ToDoPage_SortImportance,
        TaskSortKind.DueDate => Translator.ToDoPage_SortDueDate,
        TaskSortKind.MyDay => Translator.ToDoPage_SortMyDay,
        TaskSortKind.Alphabetical => Translator.ToDoPage_SortAlphabetically,
        TaskSortKind.CreationDate => Translator.ToDoPage_SortCreationDate,
        _ => Translator.ToDoPage_SortManual,
    };

    internal void ToggleImportantFilter()
    {
        if (IsReleased) return;
        ViewModel.IsImportantTasksFilterSelected = !ViewModel.IsImportantTasksFilterSelected;
    }

    partial void AppendReorderEntries(WinoMenuBuilder menu, TaskItemViewModel task);

    partial void TaskContextCellSetup(TaskCell cell)
    {
        var self = _self;
        _taskMenus[cell] = ContextMenu.Attach(cell.Root, menu =>
        {
            if (self.Value is { } view && cell.Item is { } task) view.PopulateTaskMenu(menu, task, cell);
        });
    }

    partial void TaskContextCellTeardown(TaskCell cell)
    {
        if (!_taskMenus.Remove(cell, out var menu)) return;
        menu.Dispose();
    }

    /// <summary>Windows TaskContextRequested in the macOS order; a click on an unselected row selects it first.</summary>
    internal void PopulateTaskMenu(WinoMenuBuilder menu, TaskItemViewModel task, TaskCell? cell)
    {
        if (IsReleased) return;
        if (PositionOf(task) is { } position && !_selection.IsSelected(position)) ListViews.Select(_selection, [position]);
        var self = _self;
        var editable = task.IsEditable;
        void Run(Func<ToDoPageView, Task> action)
        {
            if (self.Value is { } view) view.Observe(action(view));
        }
        menu.Item(task.MyDayActionText ?? string.Empty, () => Run(view => view.ViewModel.ToggleMyDayCommand.ExecuteAsync(task)), editable, icon: WinoIconGlyph.CalendarToday);
        menu.Item(task.ImportanceActionText ?? string.Empty, () => Run(view => view.ViewModel.ToggleImportanceCommand.ExecuteAsync(task)), editable, icon: WinoIconGlyph.Star);
        menu.Item(task.CompletionActionText ?? string.Empty, () => Run(view => view.ViewModel.ToggleTaskCommand.ExecuteAsync(task)), editable, icon: WinoIconGlyph.MarkRead);
        menu.Separator();
        menu.Item(Translator.ToDoPage_DueToday, () => Run(view => view.ViewModel.SetTaskDueDateAsync(task, ToDoPageViewModel.DuePresetDate("today"))), editable, icon: WinoIconGlyph.CalendarToday);
        menu.Item(Translator.ToDoPage_DueTomorrow, () => Run(view => view.ViewModel.SetTaskDueDateAsync(task, ToDoPageViewModel.DuePresetDate("tomorrow"))), editable, icon: WinoIconGlyph.Calendar);
        menu.Item(Translator.ToDoPage_DuePresetPickDate + "…", () => self.Value?.ShowDueDatePicker(task), editable, icon: WinoIconGlyph.Calendar);
        menu.Item(Translator.ToDoPage_RemoveDueDate, () => Run(view => view.ViewModel.SetTaskDueDateAsync(task, null)), editable && task.HasDueDate, icon: WinoIconGlyph.Calendar);
        var destinations = ViewModel.GetMoveDestinations(task);
        if (destinations is { Count: > 0 })
        {
            menu.Separator();
            menu.Submenu(Translator.ToDoPage_MoveTaskTo, move =>
            {
                foreach (var destination in destinations)
                {
                    if (destination is null) continue;
                    move.Item(destination.Title ?? string.Empty, () => Run(view => view.ViewModel.MoveTaskAsync(task, destination)), icon: WinoIconGlyph.Folder);
                }
            }, editable, WinoIconGlyph.Move);
        }
        AppendReorderEntries(menu, task);
        menu.Separator();
        menu.Item(Translator.ToDoPage_DeleteTask, () => Run(view => view.ViewModel.DeleteTaskCommand.ExecuteAsync(task)), editable,
            WinoMenus.Accelerator("Delete"), WinoIconGlyph.Delete, destructive: true);
#if DEBUG
        menu.Separator();
        menu.Item(Translator.Buttons_TestNotification, () =>
        {
            if (self.Value is { } view && task.Task is { } entity) view.Observe(view._notifications.CreateTestTaskReminderNotificationAsync(entity));
        }, icon: WinoIconGlyph.Reminder);
#endif
    }

    /// <summary>Pick a date (Windows DatePickerFlyout): a month calendar beside the row; a day sets the due date.</summary>
    internal void ShowDueDatePicker(TaskItemViewModel task)
    {
        if (IsReleased) return;
        CloseDatePicker();
        Gtk.Widget anchor = _cells.CellOf(task)?.Root ?? _list;
        var picker = _datePicker = new WinoDatePickerPopover(anchor);
        picker.SelectedDate = DateOnly.FromDateTime((task.DueDate ?? DateTime.Now).Date);
        var taskId = task.Task?.Id;
        var self = _self;
        picker.DateSelected += (_, date) => self.Value?.CommitPickedDueDate(taskId, task, date);
        picker.Popup();
    }

    private void CommitPickedDueDate(Guid? taskId, TaskItemViewModel fallback, DateOnly date)
    {
        if (IsReleased) return;
        var current = VisibleTasks.FirstOrDefault(item => item is not null && item.Task?.Id == taskId) ?? fallback;
        Observe(ViewModel.SetTaskDueDateAsync(current, date.ToDateTime(TimeOnly.MinValue)));
        var picker = _datePicker;
        _datePicker = null;
        if (picker is not null) MainLoop.Post(picker.Dispose);
    }

    private void CloseDatePicker()
    {
        var picker = _datePicker;
        _datePicker = null;
        picker?.Dispose();
    }
}
