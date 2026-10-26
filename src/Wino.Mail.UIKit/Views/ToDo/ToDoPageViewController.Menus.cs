using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.ToDo;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.ToDo;

public abstract partial class ToDoPageViewController
{
    // ---- Bar menus: filters and sort (Mac compact Filter chip), suggestions and list options ----

    private UIBarButtonItem FilterBarItem()
    {
        var menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(FilterMenuElements()))]);
        return ModeBarItems.MenuButton(WinoIconGlyph.Filter, Translator.ToDoPage_FilterOptions, menu);
    }

    private UIMenuElement[] FilterMenuElements()
    {
        var scope = new List<UIMenuElement>
        {
            ModeBarItems.Action(Translator.ToDoPage_ScopeActive, WinoIconGlyph.None, () => ViewModel.SetCompletionScopeCommand.Execute("active"),
                on: ViewModel.SelectedCompletionScope == TaskCompletionScope.Active)
        };
        if (ViewModel.IsCompletedScopeAvailable)
        {
            scope.Add(ModeBarItems.Action(Translator.ToDoPage_Completed, WinoIconGlyph.None, () => ViewModel.SetCompletionScopeCommand.Execute("completed"),
                on: ViewModel.SelectedCompletionScope == TaskCompletionScope.Completed));
            scope.Add(ModeBarItems.Action(Translator.ToDoPage_All, WinoIconGlyph.None, () => ViewModel.SetCompletionScopeCommand.Execute("all"),
                on: ViewModel.SelectedCompletionScope == TaskCompletionScope.All));
        }
        var sort = new (string Title, string Key, TaskSortKind Kind)[]
        {
            (Translator.ToDoPage_SortImportance, "importance", TaskSortKind.Importance),
            (Translator.ToDoPage_SortDueDate, "due", TaskSortKind.DueDate),
            (Translator.ToDoPage_SortMyDay, "myday", TaskSortKind.MyDay),
            (Translator.ToDoPage_SortAlphabetically, "alphabetical", TaskSortKind.Alphabetical),
            (Translator.ToDoPage_SortCreationDate, "created", TaskSortKind.CreationDate)
        }.Select(entry => (UIMenuElement)ModeBarItems.Action(entry.Title, WinoIconGlyph.None, () => ViewModel.SetSortCommand.Execute(entry.Key),
            on: ViewModel.SelectedSort == entry.Kind)).ToArray();

        return
        [
            UIMenu.Create(Translator.ToDoPage_ShowScope, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, scope.ToArray()),
            ModeBarItems.Inline(ModeBarItems.Action(Translator.ToDoPage_Important, ViewModel.IsImportantTasksFilterSelected ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star,
                () => ViewModel.IsImportantTasksFilterSelected = !ViewModel.IsImportantTasksFilterSelected, on: ViewModel.IsImportantTasksFilterSelected)),
            ModeBarItems.Submenu(Translator.ToDoPage_SortBy + ": " + ViewModel.SortDisplayText, WinoIconGlyph.ArrowSort, sort),
            ModeBarItems.Inline(ModeBarItems.Action(Translator.ToDoPage_ClearFilters, WinoIconGlyph.Dismiss, () => ViewModel.ClearFiltersCommand.Execute(null), ViewModel.IsFiltered))
        ];
    }

    /// <summary>One More button that opens its menu at once: filters and sort, then the list options (no suggestions).</summary>
    private UIBarButtonItem CombinedBarItem()
    {
        var menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(
            [ModeBarItems.Inline(FilterMenuElements()), .. OptionsMenuElements(includeSuggestions: false)]))]);
        return ModeBarItems.MenuButton(WinoIconGlyph.More, Translator.ToDoPage_ListOptions, menu);
    }

    /// <summary>My Day's suggestions as their own bar button (the iPhone bar).</summary>
    private UIBarButtonItem SuggestionsBarItem()
    {
        var menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(SuggestionElements()))]);
        return ModeBarItems.MenuButton(WinoIconGlyph.Lightbulb, Translator.ToDoPage_Suggestions, menu);
    }

    /// <summary>Up to twelve suggestions, each adding its task to My Day.</summary>
    private UIMenuElement[] SuggestionElements()
    {
        ViewModel.OpenSuggestionsCommand.Execute(null);
        var suggestions = ViewModel.Suggestions.Take(12).Select(item => (UIMenuElement)ModeBarItems.Action(
            string.IsNullOrEmpty(SuggestionCaption(item)) ? item.Title ?? string.Empty : $"{item.Title} · {SuggestionCaption(item)}",
            WinoIconGlyph.Add, () => Observe(ViewModel.AddSuggestionToMyDayCommand.ExecuteAsync(item)))).ToArray();
        return suggestions.Length > 0 ? suggestions : [ModeBarItems.Action(Translator.ToDoPage_SuggestionsEmpty, WinoIconGlyph.None, () => { }, enabled: false)];
    }

    private UIBarButtonItem OptionsBarItem()
    {
        var menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(OptionsMenuElements()))]);
        return ModeBarItems.MenuButton(WinoIconGlyph.More, Translator.ToDoPage_ListOptions, menu);
    }

    /// <summary>Suggestions (My Day) and the Windows list overflow: rename, print, email, delete.</summary>
    private UIMenuElement[] OptionsMenuElements(bool includeSuggestions = true)
    {
        var elements = new List<UIMenuElement>();
        if (includeSuggestions && ViewModel.IsMyDaySelected)
            elements.Add(ModeBarItems.Submenu(Translator.ToDoPage_Suggestions, WinoIconGlyph.Lightbulb, SuggestionElements()));
        if (ViewModel.IsNamedListSelected)
        {
            elements.Add(ModeBarItems.Inline(
                ModeBarItems.Action(Translator.ToDoPage_RenameList, WinoIconGlyph.Rename, () => Observe(ViewModel.RenameSelectedListCommand.ExecuteAsync(null)), ViewModel.CanEditSelectedList),
                ModeBarItems.Action(Translator.ToDoPage_PrintList, WinoIconGlyph.Print, () => ViewModel.PrintListCommand.Execute(null)),
                ModeBarItems.Action(Translator.ToDoPage_EmailList, WinoIconGlyph.Mail, () => ViewModel.EmailListCommand.Execute(null))));
            if (ViewModel.CanDeleteSelectedList)
                elements.Add(ModeBarItems.Inline(ModeBarItems.Action(Translator.ToDoPage_DeleteList, WinoIconGlyph.Delete,
                    () => Observe(ViewModel.DeleteListCommand.ExecuteAsync(null)), destructive: true)));
        }
        if (elements.Count == 0)
            elements.Add(ModeBarItems.Action(Translator.ToDoPage_NotAvailableYet, WinoIconGlyph.None, () => { }, enabled: false));
        return elements.ToArray();
    }

    private static string SuggestionCaption(TaskItemViewModel item)
        => string.Join(" · ", new[] { item.ListName, item.HasDueDate ? item.DueDisplayText : null }.Where(static part => !string.IsNullOrEmpty(part)));

    // ---- Swipes: leading complete; trailing delete and important ----

    private UISwipeActionsConfiguration? SwipeActions(NSIndexPath indexPath, bool leading)
    {
        if (TaskAt(indexPath) is not { } task || !task.IsEditable) return null;
        UIContextualAction[] actions = leading
            ? [ModeBarItems.Swipe(task.CompletionActionText, WinoIconGlyph.Checkmark, WinoStyle.Success, () => Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task)))]
            :
            [
                ModeBarItems.Swipe(Translator.Buttons_Delete, WinoIconGlyph.Delete, WinoToDoStyle.Critical, () => Observe(ViewModel.DeleteTaskCommand.ExecuteAsync(task)),
                    UIContextualActionStyle.Destructive),
                ModeBarItems.Swipe(task.ImportanceActionText, task.IsImportant ? WinoIconGlyph.Star : WinoIconGlyph.StarFilled, WinoToDoStyle.Important,
                    () => Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(task)))
            ];
        var configuration = UISwipeActionsConfiguration.FromActions(actions);
        configuration.PerformsFirstActionWithFullSwipe = leading;
        return configuration;
    }

    // ---- Long-press menu (Windows TaskContextRequested, Mac ContextMenuFor) ----

    private UIContextMenuConfiguration? ContextMenu(NSIndexPath indexPath)
    {
        if (TaskAt(indexPath) is not { } task) return null;
        return UIContextMenuConfiguration.Create(null, null, _ => BuildContextMenu(task));
    }

    private UIMenu BuildContextMenu(TaskItemViewModel task)
    {
        var editable = task.IsEditable;
        var groups = new List<UIMenuElement>
        {
            ModeBarItems.Inline(
                ModeBarItems.Action(task.MyDayActionText, WinoIconGlyph.WeatherSunny, () => Observe(ViewModel.ToggleMyDayCommand.ExecuteAsync(task)), editable),
                ModeBarItems.Action(task.ImportanceActionText, WinoIconGlyph.Star, () => Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(task)), editable),
                ModeBarItems.Action(task.CompletionActionText, WinoIconGlyph.Checkmark, () => Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task)), editable)),
            ModeBarItems.Inline(
                ModeBarItems.Action(Translator.ToDoPage_DueToday, WinoIconGlyph.CalendarToday, () => Observe(ViewModel.SetTaskDueDateAsync(task, ToDoPageViewModel.DuePresetDate("today"))), editable),
                ModeBarItems.Action(Translator.ToDoPage_DueTomorrow, WinoIconGlyph.Calendar, () => Observe(ViewModel.SetTaskDueDateAsync(task, ToDoPageViewModel.DuePresetDate("tomorrow"))), editable),
                ModeBarItems.Action(Translator.ToDoPage_DuePresetPickDate + "…", WinoIconGlyph.Calendar, () => PresentDueDatePicker(task), editable),
                ModeBarItems.Action(Translator.ToDoPage_RemoveDueDate, WinoIconGlyph.Calendar, () => Observe(ViewModel.SetTaskDueDateAsync(task, null)), editable && task.HasDueDate))
        };
        var destinations = ViewModel.GetMoveDestinations(task);
        if (destinations.Count > 0 && editable)
            groups.Add(ModeBarItems.Inline(ModeBarItems.Submenu(Translator.ToDoPage_MoveTaskTo, WinoIconGlyph.Move,
                destinations.Select(list => (UIMenuElement)ModeBarItems.Action(list.Title ?? string.Empty, WinoIconGlyph.Folder,
                    () => Observe(ViewModel.MoveTaskAsync(task, list)))).ToArray())));
        groups.Add(ModeBarItems.Inline(ModeBarItems.Action(Translator.ToDoPage_DeleteTask, WinoIconGlyph.Delete,
            () => Observe(ViewModel.DeleteTaskCommand.ExecuteAsync(task)), editable, destructive: true)));
        return UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, 0, groups.ToArray());
    }

    /// <summary>
    /// "Pick a date" (Windows TaskDueDatePickerHost): an inline calendar in a medium sheet; Done sets the due date, Cancel
    /// or a swipe leaves it.
    /// </summary>
    protected void PresentDueDatePicker(TaskItemViewModel task)
    {
        var picker = new UIDatePicker
        {
            Mode = UIDatePickerMode.Date,
            PreferredDatePickerStyle = UIDatePickerStyle.Inline,
            Date = (NSDate)DateTime.SpecifyKind((task.DueDate ?? DateTime.Now).Date, DateTimeKind.Local),
            TranslatesAutoresizingMaskIntoConstraints = false,
            TintColor = WinoStyle.Accent
        };
        WinoAccessibility.Label(picker, Translator.ToDoPage_DuePresetPickDate);
        var content = new UIViewController { Title = Translator.ToDoPage_DuePresetPickDate };
        var view = new UIView { BackgroundColor = UIColor.SystemBackground };
        view.AddSubview(picker);
        NSLayoutConstraint.ActivateConstraints(
        [
            picker.TopAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TopAnchor, 8),
            picker.LeadingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.LeadingAnchor, 12),
            picker.TrailingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TrailingAnchor, -12)
        ]);
        content.View = view;
        var navigation = new UINavigationController(content);
        var taskId = task.Task.Id;
        content.NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => navigation.DismissViewController(true, null));
        content.NavigationItem.RightBarButtonItem = new UIBarButtonItem(Translator.Buttons_OK, UIBarButtonItemStyle.Done, (_, _) =>
        {
            var picked = ((DateTime)picker.Date).ToLocalTime().Date;
            navigation.DismissViewController(true, null);
            // The list may have reloaded while the sheet was open; use the current item for the task.
            var current = _groups.SelectMany(group => group).FirstOrDefault(item => item.Task.Id == taskId) ?? task;
            Observe(ViewModel.SetTaskDueDateAsync(current, picked));
        });
        if (navigation.SheetPresentationController is { } sheet)
        {
            sheet.Detents = [UISheetPresentationControllerDetent.CreateLargeDetent()];
            sheet.PrefersGrabberVisible = true;
        }
        var presenter = Navigation.Shell ?? (UIViewController)this;
        while (presenter.PresentedViewController is { } presented) presenter = presented;
        presenter.PresentViewController(navigation, true, null);
    }

#if DEBUG
    private void RegisterDebugCommands()
    {
        UIKitDebugBridge.Register("todo-state", _ => Task.FromResult(
            $"groups={ViewModel.TaskGroups.Count} rows={_groups.Sum(group => group.Count)} lists={ViewModel.TaskLists.Count} accounts={ViewModel.Accounts.Count} " +
            $"ready={ViewModel.Readiness.IsReady} loading={ViewModel.IsLoading} empty={ViewModel.IsEmpty} compact={ViewModel.IsCompactLayout} " +
            $"selected={ViewModel.SelectedTasks.Count} view={ViewModel.SelectedView} list={ViewModel.SelectedList?.Title}"));
        UIKitDebugBridge.Register("todo-select", args =>
        {
            int index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            var task = _groups.SelectMany(group => group).Skip(index).FirstOrDefault();
            if (task is null) return Task.FromResult("no task " + index);
            ViewModel.SetSelectedTasks([task]);
            TaskActivated(task);
            return Task.FromResult("ok " + task.Title);
        });
        UIKitDebugBridge.Register("todo-view", async args =>
        {
            var view = Enum.Parse<TaskViewKind>(args.Length > 0 ? args[0] : nameof(TaskViewKind.MyDay), true);
            await ViewModel.SelectPresentationSurfaceAsync(view, null);
            return "ok";
        });
        UIKitDebugBridge.Register("todo-list", async args =>
        {
            int index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (index < 0 || index >= ViewModel.TaskLists.Count) return "no list " + index + " of " + ViewModel.TaskLists.Count;
            await ViewModel.SelectPresentationSurfaceAsync(TaskViewKind.All, ViewModel.TaskLists[index]);
            return "ok " + ViewModel.TaskLists[index].Title;
        });
        UIKitDebugBridge.Register("todo-add", async args =>
        {
            ViewModel.ComposerText = string.Join(' ', args);
            await ViewModel.AddTaskCommand.ExecuteAsync(null);
            return "ok";
        });
        UIKitDebugBridge.Register("todo-menu", _ =>
        {
            if (ViewModel.SelectedTask is not { } task) return Task.FromResult("no selection");
            var menu = BuildContextMenu(task);
            return Task.FromResult(string.Join(" | ", menu.Children.OfType<UIMenu>().SelectMany(group => group.Children).Select(child => child.Title)));
        });
        UIKitDebugBridge.Register("todo-pickdate-show", _ =>
        {
            if (ViewModel.SelectedTask is not { } task) return Task.FromResult("no selection");
            PresentDueDatePicker(task);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("todo-clear", _ => { ViewModel.CloseDetailCommand.Execute(null); return Task.FromResult("ok"); });
    }
#endif
}
