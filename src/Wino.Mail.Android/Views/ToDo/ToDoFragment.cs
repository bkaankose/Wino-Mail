using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Chip;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Contacts;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using IMenuItem = Wino.Core.Domain.Interfaces.IMenuItem;
using SearchView = AndroidX.AppCompat.Widget.SearchView;

namespace Wino.Mail.Android.Views.ToDo;

/// <summary>
/// The To Do mode's content page (Windows ToDoPage; canvas Material pass ToDo; Mac and iOS To Do pages) over the shared
/// <see cref="ToDoPageViewModel"/>: the 64 dp top app bar with the surface title, search (the surface filter) and the
/// overflow with the Windows filter, sort, suggestions and list options; the smart-view chips (My Day, Important,
/// Planned and Lists, which picks a list from the shared pane menu); the surface summary; the grouped tasks with round
/// checks and stars, pull to refresh (the mode's synchronization) and the long-press menu (the Windows task context
/// menu); the empty and readiness states; and the add-task bar with its due date, pinned above the keyboard. A tap opens
/// the task detail (<see cref="TaskDetailFragment"/>): full screen on compact widths, beside the list otherwise.
/// </summary>
public sealed class ToDoFragment : WinoFragment<ToDoPageViewModel>
{
    internal const string DetailKey = "todo.detail";

    private readonly List<TaskGroup> _observedGroups = [];
    private AndroidNavigationService? _navigation;
    private AndroidShellController? _shell;
    private MaterialToolbar? _toolbar;
    private ChipGroup? _chips;
    private RecyclerView? _list;
    private ToDoListAdapter? _adapter;
    private SwipeRefreshLayout? _refresh;
    private PeopleStatePanel? _empty;
    private TextView? _summary;
    private EditText? _addField;
    private WinoIconView? _addDue;
    private bool _reloadScheduled;
    private bool _chipsScheduled;

    protected override int LayoutResource => Resource.Layout.fragment_todo;

    private IShellChrome? Chrome => ParentFragment as IShellChrome;

    private bool IsCompact => Chrome?.WidthClass is null or WindowWidthClass.Compact;

    protected override void OnBind(View view, ToDoPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _navigation = services.GetService<AndroidNavigationService>();
        _shell = _navigation?.Shell;
        viewModel.SetCompactLayout(IsCompact);

        BuildToolbar(view, context, viewModel);
        _chips = view.FindViewById<ChipGroup>(Resource.Id.todo_chips);
        _summary = view.FindViewById<TextView>(Resource.Id.todo_summary);
        BuildList(view, context, viewModel);
        BuildAddBar(view, context, viewModel, dispatcher);
        if (view.FindViewById(Resource.Id.todo_empty) is { } emptyView) _empty = new PeopleStatePanel(emptyView, WinoIconGlyph.Checkmark);
        if (view.FindViewById(Resource.Id.todo_blocked_host) is { } blockedHost && view.FindViewById(Resource.Id.todo_blocked) is { } blocked)
            PeopleViews.BindReadiness(Bindings, blockedHost, blocked, viewModel.Readiness, dispatcher, WinoIconGlyph.Checkmark);
        WinoInsets.ApplyKeyboardLift(view.FindViewById(Resource.Id.todo_root) ?? view);

        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.SelectedSurfaceTitle), static vm => vm.SelectedSurfaceTitle, title =>
        {
            if (_toolbar is not null) _toolbar.Title = title ?? Translator.ToDoPage_Title;
            ScheduleChips();
        });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.SelectedSurfaceSubtitle), static vm => vm.SelectedSurfaceSubtitle, value =>
        {
            if (_summary is null) return;
            _summary.Text = value ?? string.Empty;
            _summary.Visibility = string.IsNullOrEmpty(value) ? ViewStates.Gone : ViewStates.Visible;
        });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.IsEmpty), static vm => vm.IsEmpty, _ => UpdateEmpty());
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.EmptyStateTitle), static vm => vm.EmptyStateTitle, _ => UpdateEmpty());
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.SelectedTask), static vm => vm.SelectedTask, task =>
            _adapter?.SetSelected(IsCompact ? null : task?.Id));

        NotifyCollectionChangedEventHandler menuChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(ScheduleChips);
        if (viewModel.ShellMenu?.Items is INotifyCollectionChanged items)
        {
            items.CollectionChanged += menuChanged;
            Bindings.Own(new ActionDisposable(() => items.CollectionChanged -= menuChanged));
        }
        ((INotifyCollectionChanged)viewModel.TaskGroups).CollectionChanged += GroupsChanged;
        viewModel.TaskSelectionRestored += SelectionRestored;
        Bindings.Own(new ActionDisposable(() =>
        {
            ((INotifyCollectionChanged)viewModel.TaskGroups).CollectionChanged -= GroupsChanged;
            viewModel.TaskSelectionRestored -= SelectionRestored;
            ReleaseGroups();
        }));
        if (_shell is not null)
        {
            EventHandler<string> changed = (_, property) =>
            {
                if (property is nameof(IShellMenuProvider.SynchronizationState) && _shell.CurrentProvider?.SynchronizationState.IsSynchronizing != true && _refresh is not null)
                    _refresh.Refreshing = false;
            };
            _shell.Changed += changed;
            Bindings.Own(new ActionDisposable(() => _shell.Changed -= changed));
        }
        ObserveGroups();
        ScheduleReload();
        ScheduleChips();
    }

    // ---- Top app bar: menu, title, search, overflow (Windows filter, sort, suggestions, list options) ----

    private const int MenuSearch = 1;
    private const int MenuOptions = 2;

    private void BuildToolbar(View view, Context context, ToDoPageViewModel viewModel)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.todo_toolbar);
        if (_toolbar is null) return;
        _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_menu);
        _toolbar.NavigationContentDescription = Translator.Android_Shell_OpenNavigation;
        _toolbar.NavigationClick += (_, _) => Chrome?.ToggleNavigation();
        var menu = _toolbar.Menu;
        menu.Clear();
        var search = menu.Add(0, MenuSearch, 0, Translator.ToDoPage_FilterTasks);
        if (search is not null)
        {
            search.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Find, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)));
            search.SetShowAsAction(ShowAsAction.Always | ShowAsAction.CollapseActionView);
            var field = new SearchView(context) { QueryHint = Translator.ToDoPage_FilterTasks, MaxWidth = int.MaxValue };
            field.QueryTextChange += (_, args) => { args.Handled = true; viewModel.FilterText = args.NewText ?? string.Empty; };
            field.QueryTextSubmit += (_, args) => { args.Handled = true; viewModel.FilterText = args.NewText ?? string.Empty; };
            search.SetActionView(field);
        }
        var options = menu.Add(0, MenuOptions, 1, Translator.ToDoPage_ListOptions);
        options?.SetIcon(Resource.Drawable.wino_ic_more_vert);
        options?.SetShowAsAction(ShowAsAction.Always);
        _toolbar.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == MenuOptions) ShowOptions(_toolbar.FindViewById(MenuOptions) ?? _toolbar, viewModel);
        };
        if (!string.IsNullOrEmpty(viewModel.FilterText)) viewModel.FilterText = string.Empty;
    }

    /// <summary>The Windows filter flyout, sort menu, suggestions (My Day) and list overflow as one popup.</summary>
    private void ShowOptions(View anchor, ToDoPageViewModel viewModel)
    {
        var context = anchor.RequireContext();
        var popup = new PopupMenu(context, anchor, GravityFlags.End);
        var actions = new Dictionary<int, Action>();
        int Add(global::Android.Views.IMenu menu, string title, Action run, bool enabled = true, bool? check = null)
        {
            int id = actions.Count + 1;
            actions[id] = run;
            var item = menu.Add(0, id, id, title);
            item?.SetEnabled(enabled);
            if (check is { } on) { item?.SetCheckable(true); item?.SetChecked(on); }
            return id;
        }
        var show = popup.Menu.AddSubMenu(0, 100, 100, $"{Translator.ToDoPage_ShowScope}: {viewModel.CompletionScopeDisplayText}");
        if (show is not null)
        {
            Add(show, Translator.ToDoPage_ScopeActive, () => viewModel.SetCompletionScopeCommand.Execute("active"), check: viewModel.SelectedCompletionScope == TaskCompletionScope.Active);
            if (viewModel.IsCompletedScopeAvailable)
            {
                Add(show, Translator.ToDoPage_Completed, () => viewModel.SetCompletionScopeCommand.Execute("completed"), check: viewModel.SelectedCompletionScope == TaskCompletionScope.Completed);
                Add(show, Translator.ToDoPage_All, () => viewModel.SetCompletionScopeCommand.Execute("all"), check: viewModel.SelectedCompletionScope == TaskCompletionScope.All);
            }
        }
        Add(popup.Menu, Translator.ToDoPage_Important, () => viewModel.IsImportantTasksFilterSelected = !viewModel.IsImportantTasksFilterSelected, check: viewModel.IsImportantTasksFilterSelected);
        var sort = popup.Menu.AddSubMenu(0, 101, 101, $"{Translator.ToDoPage_SortBy}: {viewModel.SortDisplayText}");
        if (sort is not null)
        {
            foreach (var (title, key, kind) in new (string, string, TaskSortKind)[]
            {
                (Translator.ToDoPage_SortImportance, "importance", TaskSortKind.Importance),
                (Translator.ToDoPage_SortDueDate, "due", TaskSortKind.DueDate),
                (Translator.ToDoPage_SortMyDay, "myday", TaskSortKind.MyDay),
                (Translator.ToDoPage_SortAlphabetically, "alphabetical", TaskSortKind.Alphabetical),
                (Translator.ToDoPage_SortCreationDate, "created", TaskSortKind.CreationDate)
            })
            {
                Add(sort, title, () => viewModel.SetSortCommand.Execute(key), check: viewModel.SelectedSort == kind);
            }
        }
        Add(popup.Menu, Translator.ToDoPage_ClearFilters, () => viewModel.ClearFiltersCommand.Execute(null), viewModel.IsFiltered);
        if (viewModel.IsMyDaySelected) Add(popup.Menu, Translator.ToDoPage_Suggestions, () => ShowSuggestions(context, viewModel));
        if (viewModel.IsNamedListSelected)
        {
            Add(popup.Menu, Translator.ToDoPage_RenameList, () => PeopleViews.Observe(viewModel.RenameSelectedListCommand.ExecuteAsync(null), ReportError), viewModel.CanEditSelectedList);
            if (viewModel.CanDeleteSelectedList) Add(popup.Menu, Translator.ToDoPage_DeleteList, () => PeopleViews.Observe(viewModel.DeleteListCommand.ExecuteAsync(null), ReportError));
        }
        Add(popup.Menu, Translator.ToDoPage_NewList, () => PeopleViews.Observe(viewModel.CreateListCommand.ExecuteAsync(null), ReportError), viewModel.CanCreateList);
        popup.MenuItemClick += (_, args) => { if (args.Item is { } item && actions.TryGetValue(item.ItemId, out var run)) run(); };
        popup.Show();
    }

    /// <summary>Windows suggestions pane: open tasks that are not in My Day; choosing one adds it.</summary>
    private void ShowSuggestions(Context context, ToDoPageViewModel viewModel)
    {
        viewModel.OpenSuggestionsCommand.Execute(null);
        var suggestions = viewModel.Suggestions.ToList();
        if (suggestions.Count == 0)
        {
            WinoChoiceSheet.Show(context, Translator.ToDoPage_Suggestions, [Translator.ToDoPage_SuggestionsEmpty], -1, _ => { });
            return;
        }
        WinoChoiceSheet.Show(context, Translator.ToDoPage_Suggestions,
            suggestions.Select(item => string.Join(" · ", new[] { item.Title, item.ListName, item.HasDueDate ? item.DueDisplayText : null }.Where(part => !string.IsNullOrEmpty(part)))).ToList(),
            -1, index => PeopleViews.Observe(viewModel.AddSuggestionToMyDayCommand.ExecuteAsync(suggestions[index]), ReportError));
    }

    // ---- Smart-view chips: My Day, Important, Planned, Lists (the shared pane menu) ----

    private void ScheduleChips()
    {
        if (_chipsScheduled || View is not { } view) return;
        _chipsScheduled = true;
        view.Post(() =>
        {
            _chipsScheduled = false;
            if (View is not null) BuildChips();
        });
    }

    private void BuildChips()
    {
        if (_chips is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        _chips.RemoveAllViews();
        // Expanded widths pin the lists pane (Windows layout): the smart views and lists are there, not in chips.
        if (_chips.Parent is View row) row.Visibility = Chrome?.WidthClass == WindowWidthClass.Expanded ? ViewStates.Gone : ViewStates.Visible;
        if (Chrome?.WidthClass == WindowWidthClass.Expanded) return;
        var items = viewModel.ShellMenu?.Items.ToArray() ?? [];
        foreach (var smart in new IMenuItem?[] { items.OfType<MyDayTaskMenuItem>().FirstOrDefault(), items.OfType<ImportantTaskMenuItem>().FirstOrDefault(), items.OfType<PlannedTaskMenuItem>().FirstOrDefault() })
        {
            if (smart is not TaskSmartViewMenuItem view) continue;
            var title = view.Parameter switch
            {
                TaskViewKind.MyDay => Translator.ToDoPage_MyDay,
                TaskViewKind.Important => Translator.ToDoPage_Important,
                _ => Translator.ToDoPage_Planned
            };
            var chip = WinoChips.Filter(context, title, viewModel.SelectedList is null && viewModel.SelectedView == view.Parameter);
            chip.Click += (_, _) =>
            {
                chip.Checked = true;
                Select(view);
            };
            _chips.AddView(chip);
        }
        // Lists: every task list in the pane order; the chip names the open list.
        var lists = ShellMenuRowPresentation.Flatten(items).Select(row => row.Item).OfType<AccountTaskListMenuItem>().ToList();
        // Lists inside collapsed groups are not in the flattened rows; add them from the groups.
        foreach (var group in items.SelectMany(Descendants).OfType<AccountTaskListMenuItem>())
            if (!lists.Contains(group)) lists.Add(group);
        if (lists.Count > 0)
        {
            var open = lists.FirstOrDefault(list => list.Parameter.Id == viewModel.SelectedList?.Id);
            var chip = WinoChips.Assist(context, open?.Title ?? Translator.Android_ToDo_Lists);
            if (open is not null)
            {
                chip.ChipBackgroundColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SecondaryContainer(context));
                chip.ChipStrokeWidth = 0;
                chip.SetTextColor(WinoColors.PrimaryText(context));
            }
            void Pick() => WinoChoiceSheet.Show(context, Translator.Android_ToDo_Lists, lists.Select(list => list.Title ?? string.Empty).ToList(),
                open is null ? -1 : lists.IndexOf(open), index => Select(lists[index]));
            chip.Click += (_, _) => Pick();
            chip.SetOnCloseIconClickListener(new ClickListener(Pick));
            _chips.AddView(chip);
        }
    }

    private static IEnumerable<IMenuItem> Descendants(IMenuItem item)
    {
        foreach (var child in ShellMenuItemRules.Children(item))
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>A chip or list choice selects through the shell like a drawer row, so the pane selection follows.</summary>
    private void Select(IMenuItem item)
    {
        if (_shell is { } shell) shell.SelectMenuItem(item);
        else if (ViewModel is { } viewModel) PeopleViews.Observe(viewModel.OnMenuItemInvokedAsync(item), ReportError);
    }

    private sealed class ClickListener(Action clicked) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => clicked();
    }

    // ---- Tasks ----

    private void BuildList(View view, Context context, ToDoPageViewModel viewModel)
    {
        _list = view.FindViewById<RecyclerView>(Resource.Id.todo_list);
        _refresh = view.FindViewById<SwipeRefreshLayout>(Resource.Id.todo_refresh);
        if (_list is null) return;
        _list.SetLayoutManager(new LinearLayoutManager(context));
        _list.ContentDescription = Translator.ToDoPage_Tasks;
        _adapter = new ToDoListAdapter(task => TaskRowPresentation.Describe(task, viewModel.TaskLists.FirstOrDefault(list => list.Id == task.Task.TaskListId)?.ColorHex));
        _adapter.TaskClicked += (_, task) => Open(task);
        _adapter.TaskLongClicked += (_, args) => ShowMenu(args.Task, args.Anchor);
        _adapter.CompletionToggled += (_, task) => PeopleViews.Observe(viewModel.ToggleTaskCommand.ExecuteAsync(task), ReportError);
        _adapter.ImportanceToggled += (_, task) => PeopleViews.Observe(viewModel.ToggleImportanceCommand.ExecuteAsync(task), ReportError);
        _list.SetAdapter(_adapter);
        if (_refresh is not null)
        {
            _refresh.SetColorSchemeColors(WinoColors.Accent(context).ToArgb());
            _refresh.Refresh += (_, _) =>
            {
                // The mode's synchronization (the Windows title-bar Sync for To Do); it ends with the provider's state.
                if (_shell is { } shell) PeopleViews.Observe(shell.SynchronizeAsync(), ReportError);
                else if (viewModel.SynchronizeCommand.CanExecute(null)) viewModel.SynchronizeCommand.Execute(null);
                _refresh.PostDelayed(() => { if (_shell?.CurrentProvider?.SynchronizationState.IsSynchronizing != true) _refresh.Refreshing = false; }, 1200);
            };
        }
    }

    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(() => { ObserveGroups(); ScheduleReload(); });

    private void GroupChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnUI(ScheduleReload);

    private void GroupPropertyChanged(object? sender, PropertyChangedEventArgs args) => OnUI(ScheduleReload);

    private void SelectionRestored(object? sender, IReadOnlyList<TaskItemViewModel> selection) => OnUI(() => _adapter?.SetSelected(IsCompact ? null : ViewModel?.SelectedTask?.Id));

    private void ObserveGroups()
    {
        ReleaseGroups();
        if (ViewModel is not { } viewModel) return;
        foreach (var group in viewModel.TaskGroups)
        {
            group.CollectionChanged += GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged += GroupPropertyChanged;
            _observedGroups.Add(group);
        }
    }

    private void ReleaseGroups()
    {
        foreach (var group in _observedGroups)
        {
            group.CollectionChanged -= GroupChanged;
            ((INotifyPropertyChanged)group).PropertyChanged -= GroupPropertyChanged;
        }
        _observedGroups.Clear();
    }

    private void OnUI(Action action)
    {
        if (Entry is { } entry) _ = entry.Dispatcher.ExecuteOnUIThread(() => { if (View is not null) action(); });
    }

    private void ScheduleReload()
    {
        if (_reloadScheduled || View is not { } view) return;
        _reloadScheduled = true;
        view.Post(() =>
        {
            _reloadScheduled = false;
            if (View is null || _adapter is null || ViewModel is not { } viewModel) return;
            _adapter.SetGroups(viewModel.TaskGroups.ToArray());
            _adapter.SetSelected(IsCompact ? null : viewModel.SelectedTask?.Id);
            UpdateEmpty();
        });
    }

    private void UpdateEmpty()
    {
        if (_empty is null || ViewModel is not { } viewModel) return;
        _empty.Visible = viewModel.IsEmpty;
        _empty.Apply(viewModel.EmptyStateTitle, viewModel.EmptyStateBody);
    }

    private void Open(TaskItemViewModel task)
    {
        if (ViewModel is not { } viewModel || _navigation is not { } navigation) return;
        viewModel.SetSelectedTasks([task]);
        // Leaving the detail clears the selection, as Windows closes its drawer (the iPhone's BackToTaskList).
        PeopleViews.Observe(navigation.ShowDetailAsync(DetailKey, static () => new TaskDetailFragment(), () =>
        {
            if (viewModel.SelectedTasks.Count > 0) viewModel.CloseDetailCommand.Execute(null);
        }), ReportError);
    }

    /// <summary>The Windows task context menu: My Day, importance, completion, due presets, Pick a date, Move to, Delete.</summary>
    private void ShowMenu(TaskItemViewModel task, View anchor)
    {
        if (ViewModel is not { } viewModel) return;
        var context = anchor.RequireContext();
        var editable = task.IsEditable;
        var popup = new PopupMenu(context, anchor, GravityFlags.End);
        var actions = new Dictionary<int, Action>();
        void Add(global::Android.Views.IMenu menu, string title, bool enabled, Action run)
        {
            int id = actions.Count + 1;
            actions[id] = run;
            menu.Add(0, id, id, title)?.SetEnabled(enabled);
        }
        Add(popup.Menu, task.MyDayActionText, editable, () => PeopleViews.Observe(viewModel.ToggleMyDayCommand.ExecuteAsync(task), ReportError));
        Add(popup.Menu, task.ImportanceActionText, editable, () => PeopleViews.Observe(viewModel.ToggleImportanceCommand.ExecuteAsync(task), ReportError));
        Add(popup.Menu, task.CompletionActionText, editable, () => PeopleViews.Observe(viewModel.ToggleTaskCommand.ExecuteAsync(task), ReportError));
        Add(popup.Menu, Translator.ToDoPage_DueToday, editable, () => PeopleViews.Observe(viewModel.SetTaskDueDateAsync(task, DateTime.Now.Date), ReportError));
        Add(popup.Menu, Translator.ToDoPage_DueTomorrow, editable, () => PeopleViews.Observe(viewModel.SetTaskDueDateAsync(task, DateTime.Now.Date.AddDays(1)), ReportError));
        Add(popup.Menu, Translator.ToDoPage_DuePresetPickDate, editable, () => TaskDatePicker.Show(this, task.DueDate, date => PeopleViews.Observe(viewModel.SetTaskDueDateAsync(task, date), ReportError)));
        if (task.HasDueDate) Add(popup.Menu, Translator.ToDoPage_RemoveDueDate, editable, () => PeopleViews.Observe(viewModel.SetTaskDueDateAsync(task, null), ReportError));
        var destinations = viewModel.TaskLists.Where(list => !list.IsReadOnly && list.Id != task.Task.TaskListId &&
            list.MailAccountId == task.Task.MailAccountId && list.SourceKind == task.Task.SourceKind).ToList();
        if (destinations.Count > 0 && editable)
        {
            Add(popup.Menu, Translator.ToDoPage_MoveTaskTo, true, () => WinoChoiceSheet.Show(context, Translator.ToDoPage_MoveTaskTo,
                destinations.Select(list => list.Title ?? string.Empty).ToList(), -1, index => PeopleViews.Observe(viewModel.MoveTaskAsync(task, destinations[index]), ReportError)));
        }
        Add(popup.Menu, Translator.ToDoPage_DeleteTask, editable, () => PeopleViews.Observe(viewModel.DeleteTaskCommand.ExecuteAsync(task), ReportError));
        popup.MenuItemClick += (_, args) => { if (args.Item is { } item && actions.TryGetValue(item.ItemId, out var run)) run(); };
        popup.Show();
    }

    // ---- Add-task bar (Windows quick add; pinned above the keyboard) ----

    private void BuildAddBar(View view, Context context, ToDoPageViewModel viewModel, IDispatcher dispatcher)
    {
        _addField = view.FindViewById<EditText>(Resource.Id.todo_add_field);
        _addDue = view.FindViewById<WinoIconView>(Resource.Id.todo_add_due);
        if (view.FindViewById<WinoIconView>(Resource.Id.todo_add_glyph) is { } glyph)
        {
            glyph.Icon = WinoIconGlyph.Add;
            glyph.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
            glyph.SetTextColor(WinoColors.AccentText(context));
        }
        if (_addField is { } field)
        {
            field.TextChanged += (_, _) => { if (viewModel.ComposerText != (field.Text ?? string.Empty)) viewModel.ComposerText = field.Text ?? string.Empty; };
            field.FocusChange += (_, args) =>
            {
                if (args.HasFocus) viewModel.IsComposerExpanded = true;
                else if (string.IsNullOrWhiteSpace(viewModel.ComposerText)) viewModel.IsComposerExpanded = false;
            };
            field.EditorAction += (_, args) =>
            {
                args.Handled = args.ActionId is ImeAction.Done or ImeAction.Go or ImeAction.Send || args.Event?.KeyCode == Keycode.Enter;
                if (args.Handled) Submit(viewModel);
            };
        }
        if (_addDue is { } due)
        {
            due.Icon = WinoIconGlyph.Calendar;
            due.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
            due.ContentDescription = Translator.ToDoPage_DueDate;
            due.Clickable = true;
            due.Click += (_, _) => TaskDatePicker.Show(this, viewModel.ComposerDueDate, date => viewModel.ComposerDueDate = date);
        }
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.ComposerPlaceholder), static vm => vm.ComposerPlaceholder, value => { if (_addField is not null) _addField.Hint = value ?? Translator.ToDoPage_AddTaskPlaceholder; });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.ComposerText), static vm => vm.ComposerText, value =>
        {
            if (_addField is not null && _addField.Text != (value ?? string.Empty)) _addField.Text = value ?? string.Empty;
        });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.ComposerDueDate), static vm => vm.ComposerDueDate, value =>
        {
            if (_addDue is null) return;
            _addDue.SetTextColor(value is null ? WinoColors.SecondaryText(context) : WinoColors.AccentText(context));
            _addDue.ContentDescription = value is { } date ? $"{Translator.ToDoPage_DueDate}: {date:d}" : Translator.ToDoPage_DueDate;
        });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.CanCreateTask), static vm => vm.CanCreateTask, value => { if (_addField is not null) _addField.Enabled = value; });
        Bind(viewModel, dispatcher, nameof(ToDoPageViewModel.IsQuickAddVisible), static vm => vm.IsQuickAddVisible, visible =>
        {
            if (view.FindViewById(Resource.Id.todo_add_strip) is { } strip) strip.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        });
        EventHandler<Wino.Core.Domain.Models.TaskComposerFocusRequestedEventArgs> focus = (_, _) => OnUI(() =>
        {
            if (_addField is null) return;
            _addField.RequestFocus();
            (context.GetSystemService(Context.InputMethodService) as InputMethodManager)?.ShowSoftInput(_addField, ShowFlags.Implicit);
        });
        viewModel.TaskComposerFocusRequested += focus;
        Bindings.Own(new ActionDisposable(() => viewModel.TaskComposerFocusRequested -= focus));
    }

    private void Submit(ToDoPageViewModel viewModel)
    {
        if (string.IsNullOrWhiteSpace(viewModel.ComposerText) || !viewModel.AddTaskCommand.CanExecute(null)) return;
        PeopleViews.Observe(viewModel.AddTaskCommand.ExecuteAsync(null), ReportError);
    }
}
