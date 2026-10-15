using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Contacts;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.Controls.Android.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.ToDo;

/// <summary>
/// The task detail (Windows ToDoPage detail drawer, Mac and iOS task details; canvas Material pass TaskDetail) over the
/// shared <see cref="ToDoPageViewModel.SelectedTask"/>. Not a routed page: <see cref="ToDoFragment"/> places it through
/// <see cref="AndroidNavigationService.ShowDetailAsync"/>, full screen on compact widths (Back) and beside the list
/// otherwise (Close). Inline fields in the Windows order: the check, the editable title and the star; Steps (progress,
/// check, editable title and remove per step, Add step); My Day; the due date (Today, Tomorrow, Next week, Pick a date,
/// Remove); Notes; Create calendar event; the created line; Delete in the top app bar. It closes when the selection
/// empties. Read-only tasks disable every editor.
/// </summary>
public sealed class TaskDetailFragment : WinoFragment
{
    private const int MenuDelete = 1;

    private ToDoPageViewModel? _viewModel;
    private AndroidNavigationService? _navigation;
    private BindingScope? _taskScope;
    private TaskItemViewModel? _task;
    private MaterialToolbar? _toolbar;
    private WinoRoundCheckbox? _check;
    private EditText? _title;
    private WinoIconView? _star;
    private TextView? _status;
    private LinearProgressIndicator? _progress;
    private TextView? _stepsSummary;
    private LinearLayout? _steps;
    private MaterialButton? _addStep;
    private (View Row, WinoIconView Icon, TextView Title, TextView Value) _myDay;
    private (View Row, WinoIconView Icon, TextView Title, TextView Value) _due;
    private TextInputLayout? _notes;
    private MaterialButton? _calendar;
    private TextView? _created;
    private bool _compact;

    protected override int LayoutResource => Resource.Layout.fragment_task_detail;

    protected override void OnBind(View view)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        if (Entry is not { } entry) return;
        var context = view.RequireContext();
        var dispatcher = entry.Dispatcher;
        _viewModel = services.GetRequiredService<ToDoPageViewModel>();
        _navigation = services.GetService<AndroidNavigationService>();
        _compact = services.GetRequiredService<IWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        if (view.FindViewById<WinoMaxWidthLayout>(Resource.Id.task_detail_column) is { } column)
            column.MaxWidthDp = WinoTokens.Dp(context, Resource.Dimension.wino_people_card_max_width);

        BuildToolbar(view, context);
        if (view.FindViewById<LinearLayout>(Resource.Id.task_detail_content) is { } content) BuildContent(context, content, _viewModel);

        Bindings.Own(new PropertyBinding<ToDoPageViewModel, TaskItemViewModel?>(_viewModel, nameof(ToDoPageViewModel.SelectedTask),
            static vm => vm.SelectedTask, BindTask, dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ToDoPageViewModel, int>(_viewModel, nameof(ToDoPageViewModel.SelectedTaskCount),
            static vm => vm.SelectedTaskCount, count =>
            {
                if (count == 0 && _navigation is { CurrentDetailKey: ToDoFragment.DetailKey } navigation) PeopleViews.Observe(navigation.CloseRenderingFrameAsync(), ReportError);
            }, dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ToDoPageViewModel, bool>(_viewModel, nameof(ToDoPageViewModel.CanEditSelectedTask),
            static vm => vm.CanEditSelectedTask, ApplyEditable, dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ToDoPageViewModel, bool>(_viewModel, nameof(ToDoPageViewModel.CanCreateCalendarEvent),
            static vm => vm.CanCreateCalendarEvent, value => { if (_calendar is not null) _calendar.Visibility = value ? ViewStates.Visible : ViewStates.Gone; }, dispatcher, ReportError));
        Bindings.Own(new ActionDisposable(() =>
        {
            // Edits in progress are saved when the view goes (back, rotation).
            CommitTitle();
            CommitNotes();
            _taskScope?.Dispose();
            _taskScope = null;
            _task = null;
        }));
    }

    // ---- Top app bar ----

    private void BuildToolbar(View view, Context context)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.task_detail_toolbar);
        if (_toolbar is null) return;
        _toolbar.SetTitleTextAppearance(context, Resource.Style.Widget_Wino_Tasks_DetailBarTitle);
        if (_compact)
        {
            WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.task_detail_root) ?? view, top: true);
            _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_arrow_back);
            _toolbar.NavigationContentDescription = Translator.ToDoPage_Back;
            _toolbar.NavigationClick += (_, _) => Activity?.OnBackPressedDispatcher.OnBackPressed();
        }
        else
        {
            _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_close);
            _toolbar.NavigationContentDescription = Translator.ToDoPage_CloseDetails;
            _toolbar.NavigationClick += (_, _) => _viewModel?.CloseDetailCommand.Execute(null);
        }
        _toolbar.Menu.Clear();
        _toolbar.Menu.Add(0, MenuDelete, 0, Translator.ToDoPage_DeleteTask)?
            .SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Delete, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)))?
            .SetShowAsAction(ShowAsAction.Always);
        _toolbar.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == MenuDelete && _task is { } task && _viewModel is { } viewModel)
                PeopleViews.Observe(viewModel.DeleteTaskCommand.ExecuteAsync(task), ReportError);
        };
    }

    // ---- Content ----

    private void BuildContent(Context context, LinearLayout content, ToDoPageViewModel viewModel)
    {
        // Header: check, title, star; the status line below.
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.Top);
        int touchInset = (WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large) - WinoTokens.Px(context, Resource.Dimension.wino_tasks_check)) / 2;
        header.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_space_l) - touchInset, 0, WinoTokens.Px(context, Resource.Dimension.wino_space_s), 0);
        _check = new WinoRoundCheckbox(context, Resource.Dimension.wino_tasks_check);
        _check.Toggled += (_, _) => { if (_task is { } task) PeopleViews.Observe(viewModel.ToggleTaskCommand.ExecuteAsync(task), ReportError); };
        header.AddView(_check);
        _title = new EditText(context) { Background = null, InputType = InputTypes.ClassText | InputTypes.TextFlagCapSentences | InputTypes.TextFlagMultiLine, ImeOptions = ImeAction.Done };
        WinoTokens.Style(_title, WinoTextStyle.ItemTitle);
        _title.SetTextSize(global::Android.Util.ComplexUnitType.Px, WinoTokens.PxF(context, Resource.Dimension.wino_tasks_detail_title_text));
        _title.SetHorizontallyScrolling(false);
        _title.SetMaxLines(4);
        _title.ContentDescription = Translator.ToDoPage_Tasks;
        _title.SetRawInputType(InputTypes.ClassText | InputTypes.TextFlagCapSentences);
        _title.EditorAction += (_, args) =>
        {
            args.Handled = true;
            _title.ClearFocus();
            HideKeyboard(_title);
        };
        _title.FocusChange += (_, args) => { if (!args.HasFocus) CommitTitle(); };
        header.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
        {
            MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_tasks_row_gap) - touchInset
        });
        _star = new WinoIconView(context) { GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_tasks_star_glyph), Clickable = true, Focusable = true, ImportantForAccessibility = ImportantForAccessibility.Yes };
        _star.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, ShellViews.BorderlessRipple(context));
        _star.Click += (_, _) => { if (_task is { } task) PeopleViews.Observe(viewModel.ToggleImportanceCommand.ExecuteAsync(task), ReportError); };
        int touch = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
        header.AddView(_star, new LinearLayout.LayoutParams(touch, touch));
        content.AddView(header);
        _status = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
        _status.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_indent), 0, WinoTokens.Px(context, Resource.Dimension.wino_space_l), WinoTokens.Px(context, Resource.Dimension.wino_space_s));
        content.AddView(_status);

        // Steps.
        var stepsHeader = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        stepsHeader.SetGravity(GravityFlags.CenterVertical);
        stepsHeader.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_indent), 0, WinoTokens.Px(context, Resource.Dimension.wino_space_l), 0);
        stepsHeader.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height));
        var stepsTitle = WinoTokens.Style(new TextView(context) { Text = Translator.ToDoPage_Steps }, WinoTextStyle.Subheader);
        stepsTitle.SetTextColor(WinoColors.AccentText(context));
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(stepsTitle, true);
        stepsHeader.AddView(stepsTitle);
        _progress = new LinearProgressIndicator(context) { Max = 100 };
        _progress.SetIndicatorColor(WinoColors.Accent(context).ToArgb());
        stepsHeader.AddView(_progress, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
        {
            MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_m),
            MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_m)
        });
        _stepsSummary = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
        stepsHeader.AddView(_stepsSummary);
        content.AddView(stepsHeader);
        _steps = new LinearLayout(context) { Orientation = Orientation.Vertical };
        content.AddView(_steps);
        _addStep = TextButton(context, Translator.ToDoPage_AddStep, WinoIconGlyph.Add, () => PeopleViews.Observe(viewModel.AddStepCommand.ExecuteAsync(null), ReportError));
        _addStep.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_indent) - WinoTokens.Px(context, Resource.Dimension.wino_space_l), _addStep.PaddingTop, _addStep.PaddingEnd, _addStep.PaddingBottom);
        content.AddView(_addStep, Gutter(context));

        content.AddView(Divider(context));

        // Schedule: My Day and the due date (Remind me and Repeat are "coming soon" on Windows and left out).
        _myDay = SettingRow(context, WinoIconGlyph.WeatherSunny, () => { if (_task is { } task) PeopleViews.Observe(viewModel.ToggleMyDayCommand.ExecuteAsync(task), ReportError); });
        content.AddView(_myDay.Row);
        _due = SettingRow(context, WinoIconGlyph.Calendar, () => ShowDueMenu(context, viewModel));
        content.AddView(_due.Row);

        content.AddView(Divider(context));

        // Notes.
        _notes = SettingsBinder.CreateTextField(context, Translator.ToDoPage_Notes, multiline: true);
        _notes.PlaceholderText = Translator.ToDoPage_AddNote;
        _notes.EditText?.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_notes_min_height));
        if (_notes.EditText is { } notesField) notesField.FocusChange += (_, args) => { if (!args.HasFocus) CommitNotes(); };
        var notesParameters = Gutter(context);
        notesParameters.TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        content.AddView(_notes, notesParameters);

        _calendar = TextButton(context, Translator.ToDoPage_CreateCalendarEvent, WinoIconGlyph.CalendarToday, () => PeopleViews.Observe(viewModel.CreateCalendarEventCommand.ExecuteAsync(null), ReportError));
        content.AddView(_calendar, Gutter(context));

        _created = WinoTokens.Style(new TextView(context), WinoTextStyle.Caption);
        WinoTokens.Pad(_created, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_l, 0);
        content.AddView(_created);
    }

    private static LinearLayout.LayoutParams Gutter(Context context)
    {
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        int gutter = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        parameters.SetMargins(gutter, 0, gutter, 0);
        return parameters;
    }

    private static View Divider(Context context)
    {
        var divider = new View(context);
        divider.SetBackgroundColor(WinoColors.Divider(context));
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_stroke_hairline));
        int gutter = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        parameters.SetMargins(gutter, WinoTokens.Px(context, Resource.Dimension.wino_space_s), gutter, WinoTokens.Px(context, Resource.Dimension.wino_space_s));
        divider.LayoutParameters = parameters;
        return divider;
    }

    private static MaterialButton TextButton(Context context, string title, WinoIconGlyph glyph, Action run)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = title };
        button.Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.AccentText(context));
        WinoMaterialAccent.Tint(button, filled: false);
        button.Gravity = GravityFlags.Start | GravityFlags.CenterVertical;
        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>A 56 dp setting row (board TaskDetail): glyph, title and a supporting value; accent while set.</summary>
    private static (View Row, WinoIconView Icon, TextView Title, TextView Value) SettingRow(Context context, WinoIconGlyph glyph, Action run)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_row_height));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s);
        row.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, ShellViews.Ripple(context));
        var icon = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph) };
        row.AddView(icon, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_indent) - WinoTokens.Px(context, Resource.Dimension.wino_space_l), ViewGroup.LayoutParams.WrapContent));
        var text = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var title = WinoTokens.Style(new TextView(context), WinoTextStyle.ItemTitle);
        var value = WinoTokens.Style(new TextView(context), WinoTextStyle.ItemSupporting);
        text.AddView(title);
        text.AddView(value);
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.Click += (_, _) => run();
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return (row, icon, title, value);
    }

    private void ShowDueMenu(Context context, ToDoPageViewModel viewModel)
    {
        if (_task is not { } task || !viewModel.CanEditSelectedTask) return;
        var popup = new PopupMenu(context, _due.Row, GravityFlags.Start);
        popup.Menu.Add(0, 1, 1, Translator.ToDoPage_DuePresetToday);
        popup.Menu.Add(0, 2, 2, Translator.ToDoPage_DuePresetTomorrow);
        popup.Menu.Add(0, 3, 3, Translator.ToDoPage_DuePresetNextWeek);
        popup.Menu.Add(0, 4, 4, Translator.ToDoPage_DuePresetPickDate);
        if (task.HasDueDate) popup.Menu.Add(0, 5, 5, Translator.ToDoPage_RemoveDueDate);
        popup.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case 1: PeopleViews.Observe(viewModel.SetDuePresetCommand.ExecuteAsync("today"), ReportError); break;
                case 2: PeopleViews.Observe(viewModel.SetDuePresetCommand.ExecuteAsync("tomorrow"), ReportError); break;
                case 3: PeopleViews.Observe(viewModel.SetDuePresetCommand.ExecuteAsync("nextweek"), ReportError); break;
                case 4: TaskDatePicker.Show(this, task.DueDate, date => PeopleViews.Observe(viewModel.SetTaskDueDateAsync(task, date), ReportError)); break;
                case 5: PeopleViews.Observe(viewModel.SetDuePresetCommand.ExecuteAsync("none"), ReportError); break;
            }
        };
        popup.Show();
    }

    // ---- Binding to the selected task ----

    private void BindTask(TaskItemViewModel? task)
    {
        if (ReferenceEquals(task, _task)) return;
        CommitTitle();
        CommitNotes();
        _taskScope?.Dispose();
        _taskScope = null;
        _task = task;
        if (task is null || Entry is not { } entry || View?.Context is not { } context) return;
        var scope = _taskScope = new BindingScope();
        void On<TValue>(string property, Func<TaskItemViewModel, TValue> read, Action<TValue> apply)
            => scope.Own(new PropertyBinding<TaskItemViewModel, TValue>(task, property, read, apply, entry.Dispatcher, ReportError));

        On(nameof(task.Title), static item => item.Title, value => { if (_title is { IsFocused: false }) _title.Text = value ?? string.Empty; });
        On(nameof(task.IsCompleted), static item => item.IsCompleted, value =>
        {
            if (_check is not null)
            {
                _check.Checked = value;
                _check.Label = task.CompletionActionText;
            }
            if (_title is not null)
            {
                _title.SetTextColor(value ? WinoColors.TertiaryText(context) : WinoColors.PrimaryText(context));
                _title.PaintFlags = value ? _title.PaintFlags | PaintFlags.StrikeThruText : _title.PaintFlags & ~PaintFlags.StrikeThruText;
            }
        });
        On(nameof(task.IsImportant), static item => item.IsImportant, value =>
        {
            if (_star is null) return;
            _star.Icon = value ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star;
            _star.SetTextColor(value ? WinoColors.AccentText(context) : WinoColors.SecondaryText(context));
            _star.ContentDescription = task.ImportanceActionText;
        });
        On(nameof(task.DueDisplayText), static item => item.DueDisplayText, _ => ApplySchedule(context, task));
        On(nameof(task.IsOverdue), static item => item.IsOverdue, _ => ApplySchedule(context, task));
        On(nameof(task.IsInMyDay), static item => item.IsInMyDay, _ => ApplySchedule(context, task));
        On(nameof(task.ListName), static item => item.ListName, value => { if (_toolbar is not null) _toolbar.Title = value ?? string.Empty; ApplySchedule(context, task); });
        On(nameof(task.ShowSummaryListName), static item => item.ShowSummaryListName, _ => ApplySchedule(context, task));
        On(nameof(task.StepSummaryText), static item => item.StepSummaryText, _ => ApplySteps(task));
        On(nameof(task.Notes), static item => item.Notes, value =>
        {
            if (_notes?.EditText is { IsFocused: false } field && field.Text != (value ?? string.Empty)) field.Text = value ?? string.Empty;
        });
        On(nameof(task.CreatedOnText), static item => item.CreatedOnText, value => { if (_created is not null) _created.Text = value ?? string.Empty; });
        NotifyCollectionChangedEventHandler stepsChanged = (_, _) => _ = entry.Dispatcher.ExecuteOnUIThread(() => { if (View is not null && ReferenceEquals(_task, task)) RebuildSteps(task); });
        task.Steps.CollectionChanged += stepsChanged;
        scope.Own(new ActionDisposable(() => task.Steps.CollectionChanged -= stepsChanged));
        RebuildSteps(task);
        ApplyEditable(_viewModel?.CanEditSelectedTask == true);
    }

    private void ApplySchedule(Context context, TaskItemViewModel task)
    {
        if (_status is not null)
        {
            _status.Text = TaskRowPresentation.DetailSummary(task);
            _status.SetTextColor(task.IsOverdue ? WinoColors.Critical(context) : WinoColors.SecondaryText(context));
            _status.Visibility = task.HasDetailSummary ? ViewStates.Visible : ViewStates.Gone;
        }
        var accent = WinoColors.AccentText(context);
        _myDay.Title.Text = task.IsInMyDay ? Translator.ToDoPage_AddedToMyDay : Translator.ToDoPage_AddToMyDay;
        _myDay.Title.SetTextColor(task.IsInMyDay ? accent : WinoColors.PrimaryText(context));
        _myDay.Icon.SetTextColor(task.IsInMyDay ? accent : WinoColors.SecondaryText(context));
        _myDay.Value.Visibility = task.IsInMyDay ? ViewStates.Visible : ViewStates.Gone;
        _myDay.Value.Text = task.MyDayActionText;
        _myDay.Row.ContentDescription = task.MyDayActionText;
        var dueColor = task.IsOverdue ? WinoColors.Critical(context) : accent;
        _due.Title.Text = task.HasDueDate ? task.DueDisplayText : Translator.ToDoPage_AddDueDate;
        _due.Title.SetTextColor(task.HasDueDate ? dueColor : WinoColors.PrimaryText(context));
        _due.Icon.SetTextColor(task.HasDueDate ? dueColor : WinoColors.SecondaryText(context));
        _due.Value.Visibility = task.DueDate is null ? ViewStates.Gone : ViewStates.Visible;
        _due.Value.Text = task.DueDate is { } due ? due.ToString("D") : string.Empty;
    }

    private void ApplySteps(TaskItemViewModel task)
    {
        bool has = task.HasSteps;
        if (_progress is not null)
        {
            _progress.Visibility = has ? ViewStates.Visible : ViewStates.Invisible;
            _progress.Progress = task.StepCount == 0 ? 0 : (int)Math.Round(100.0 * task.CompletedStepCount / task.StepCount);
        }
        if (_stepsSummary is not null)
        {
            _stepsSummary.Text = has ? task.StepSummaryText : string.Empty;
            _stepsSummary.Visibility = has ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    private BindingScope? _stepsScope;

    private void RebuildSteps(TaskItemViewModel task)
    {
        if (_steps is null || _viewModel is not { } viewModel || View?.Context is not { } context || Entry is not { } entry) return;
        _stepsScope?.Dispose();
        var scope = _stepsScope = new BindingScope();
        _taskScope?.Own(new ActionDisposable(() => scope.Dispose()));
        _steps.RemoveAllViews();
        int touchInset = (WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large) - WinoTokens.Px(context, Resource.Dimension.wino_tasks_step_check)) / 2;
        foreach (var step in task.Steps.ToList())
        {
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_height));
            row.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_tasks_detail_step_indent) - touchInset, 0, WinoTokens.Px(context, Resource.Dimension.wino_space_s), 0);
            var check = new WinoRoundCheckbox(context, Resource.Dimension.wino_tasks_step_check) { Checked = step.IsCompleted, Enabled = step.IsEditable, Label = step.Title };
            check.Toggled += (_, _) => PeopleViews.Observe(viewModel.ToggleStepCommand.ExecuteAsync(step), ReportError);
            row.AddView(check);
            var field = new EditText(context) { Background = null, Text = step.Title ?? string.Empty, Enabled = step.IsEditable, ImeOptions = ImeAction.Done };
            field.SetSingleLine(true);
            WinoTokens.Style(field, WinoTextStyle.BodyLarge);
            field.ContentDescription = Translator.ToDoPage_Steps;
            void CommitStep()
            {
                var title = field.Text?.Trim() ?? string.Empty;
                if (title.Length == 0 || title == step.Title) { field.Text = step.Title ?? string.Empty; return; }
                step.Title = title;
                PeopleViews.Observe(viewModel.SaveStepCommand.ExecuteAsync(step), ReportError);
            }
            field.EditorAction += (_, args) => { args.Handled = true; field.ClearFocus(); HideKeyboard(field); };
            field.FocusChange += (_, args) => { if (!args.HasFocus) CommitStep(); };
            row.AddView(field, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            var remove = new WinoIconView(context) { Icon = WinoIconGlyph.Dismiss, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s), Clickable = true, Focusable = true, ContentDescription = Translator.ToDoPage_DeleteStep, ImportantForAccessibility = ImportantForAccessibility.Yes, Enabled = step.IsEditable };
            remove.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, ShellViews.BorderlessRipple(context));
            remove.Click += (_, _) => PeopleViews.Observe(viewModel.DeleteStepCommand.ExecuteAsync(step), ReportError);
            int touch = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
            row.AddView(remove, new LinearLayout.LayoutParams(touch, touch));
            scope.Own(new PropertyBinding<TaskStepViewModel, bool>(step, nameof(step.IsCompleted), static item => item.IsCompleted, value =>
            {
                check.Checked = value;
                field.SetTextColor(value ? WinoColors.TertiaryText(context) : WinoColors.PrimaryText(context));
                field.PaintFlags = value ? field.PaintFlags | PaintFlags.StrikeThruText : field.PaintFlags & ~PaintFlags.StrikeThruText;
            }, entry.Dispatcher, ReportError));
            scope.Own(new PropertyBinding<TaskStepViewModel, string>(step, nameof(step.Title), static item => item.Title,
                value => { if (!field.IsFocused) field.Text = value ?? string.Empty; }, entry.Dispatcher, ReportError));
            _steps.AddView(row);
        }
        ApplySteps(task);
    }

    private void ApplyEditable(bool editable)
    {
        if (_check is not null) _check.Enabled = editable;
        if (_title is not null) _title.Enabled = editable;
        if (_star is not null) _star.Enabled = editable;
        if (_addStep is not null) _addStep.Enabled = editable;
        if (_notes is not null) _notes.Enabled = editable;
        if (_myDay.Row is not null) _myDay.Row.Enabled = editable;
        if (_due.Row is not null) _due.Row.Enabled = editable;
        _toolbar?.Menu.FindItem(MenuDelete)?.SetVisible(editable);
    }

    private void CommitTitle()
    {
        if (_task is not { } task || _title is null || _viewModel is not { CanEditSelectedTask: true } viewModel) return;
        var title = _title.Text?.Trim() ?? string.Empty;
        if (title.Length == 0) { _title.Text = task.Title ?? string.Empty; return; }
        if (title == task.Title) return;
        task.Title = title;
        PeopleViews.Observe(viewModel.SaveTaskCommand.ExecuteAsync(task), ReportError);
    }

    private void CommitNotes()
    {
        if (_task is not { } task || _notes?.EditText is not { } field || _viewModel is not { CanEditSelectedTask: true } viewModel) return;
        var notes = field.Text ?? string.Empty;
        if (notes == (task.Notes ?? string.Empty)) return;
        task.Notes = notes;
        PeopleViews.Observe(viewModel.SaveTaskCommand.ExecuteAsync(task), ReportError);
    }

    private static void HideKeyboard(View view)
        => (view.Context?.GetSystemService(Context.InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
}
