using System.Collections.Specialized;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.ToDo;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Contacts;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.ToDo;

/// <summary>
/// The task detail drawer (Windows ToDoPage detail drawer, Mac ToDoPageViewController.Detail) over the shared
/// <see cref="ToDoPageViewModel.SelectedTask"/>: a pinned header (round checkbox, editable title, star, status line), then
/// Steps (progress, step rows, Add step), Schedule (My Day, due date presets and picker, Remind me and Repeat as "Soon",
/// Delete task), Notes, Create calendar event and the provenance line. A multiple selection shows the bulk actions instead.
/// The iPhone pushes it; the iPad shows it in the reader column, with an empty state while nothing is selected.
/// </summary>
public sealed class ToDoTaskDetailViewController : UIViewController
{
    private readonly ToDoPageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception> _error;
    private readonly Action<TaskItemViewModel> _pickDate;
    private readonly bool _showsClose;
    private readonly BindingScope _bindings = new();
    private BindingScope? _taskScope;
    private BindingScope? _stepsScope;
    private readonly WinoRoundCheckbox _check = new(22);
    private readonly UITextView _title = new() { ScrollEnabled = false, BackgroundColor = UIColor.Clear, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _star = WinoToDoStyle.IconButton(WinoIconGlyph.Star, string.Empty, 18, 44, 44, WinoStyle.SecondaryText);
    private readonly UILabel _status = WinoToDoStyle.Caption();
    private readonly UIProgressView _stepsProgress = new(UIProgressViewStyle.Default) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _stepsSummary = WinoToDoStyle.Caption();
    private readonly UIStackView _steps = WinoLayout.VStack(4);
    private readonly UIButton _addStep;
    private readonly UIButton _myDay;
    private readonly UIButton _due;
    private readonly UIButton _delete;
    private readonly UIButton _calendar;
    private readonly UITextView _notes = new() { BackgroundColor = UIColor.Clear, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _notesPlaceholder = WinoStyle.Label(Translator.ToDoPage_AddNote, WinoStyle.Body, WinoStyle.TertiaryText);
    private readonly UILabel _created = WinoToDoStyle.Caption(null, WinoStyle.TertiaryText);
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };
    private readonly UIView _single = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIView _multi = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UILabel _multiCount = WinoStyle.Label(string.Empty, WinoStyle.Heading);
    private readonly List<UIButton> _multiActions = new();
    private readonly WinoStateView _empty = new(WinoIconGlyph.Checkmark, string.Empty, Translator.ToDoPage_Tasks, WinoStyle.TertiaryText) { Hidden = true };
    private TaskItemViewModel? _task;
    private bool _released;

    internal ToDoTaskDetailViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, Action<Exception> error,
        Action<TaskItemViewModel> pickDate, bool showsClose)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _error = error;
        _pickDate = pickDate;
        _showsClose = showsClose;
        _addStep = Row(WinoIconGlyph.Add, Translator.ToDoPage_AddStep, WinoStyle.Accent, () => Observe(_viewModel.AddStepCommand.ExecuteAsync(null)));
        _myDay = Row(WinoIconGlyph.WeatherSunny, Translator.ToDoPage_AddToMyDay, WinoStyle.Accent, () => { if (_task is { } task) Observe(_viewModel.ToggleMyDayCommand.ExecuteAsync(task)); });
        _due = Row(WinoIconGlyph.Calendar, Translator.ToDoPage_AddDueDate, WinoStyle.Accent, null);
        _due.ShowsMenuAsPrimaryAction = true;
        _due.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(DueMenu()))]);
        _delete = Row(WinoIconGlyph.Delete, Translator.ToDoPage_DeleteTask, WinoToDoStyle.Critical, () => { if (_task is { } task) Observe(_viewModel.DeleteTaskCommand.ExecuteAsync(task)); });
        _calendar = Row(WinoIconGlyph.Calendar, Translator.ToDoPage_CreateCalendarEvent, WinoStyle.Accent, () => Observe(_viewModel.CreateCalendarEventCommand.ExecuteAsync(null)));
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var content = root;
        // The iPad reader column already frames its page in a Wino zone; the iPhone push gets its own.
        if (!_showsClose)
        {
            var zone = new WinoZoneView();
            root.AddSubview(zone);
            var guide = root.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
                zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
                zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
                zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
            ]);
            content = zone.ContentView;
        }
        WinoLayout.Fill(_single, content);
        WinoLayout.Fill(_multi, content);
        _empty.Place(content);
        BuildSingle();
        BuildMulti();
        View = root;
    }

    private void BuildSingle()
    {
        // Pinned header (Windows padding 10,8; card fill, bottom stroke).
        var header = new WinoSurfaceView { Fill = WinoToDoStyle.CardFill };
        _check.Toggled += (_, _) => { if (_task is { } task) Observe(_viewModel.ToggleTaskCommand.ExecuteAsync(task)); };
        _title.Font = WinoStyle.Scaled(20, UIFontWeight.Semibold);
        _title.TextContainerInset = UIEdgeInsets.Zero;
        _title.TextContainer.LineFragmentPadding = 0;
        _title.ReturnKeyType = UIReturnKeyType.Done;
        _title.Delegate = new TitleDelegate(this);
        WinoLayout.Priorities(_title, UILayoutConstraintAxis.Horizontal, 1, 250);
        WinoAccessibility.Label(_title, Translator.ToDoPage_Tasks);
        _star.PrimaryActionTriggered += (_, _) => { if (_task is { } task) Observe(_viewModel.ToggleImportanceCommand.ExecuteAsync(task)); };
        var titleRow = WinoLayout.HStack(10, _check, _title, _star);
        titleRow.Alignment = UIStackViewAlignment.Top;
        if (_showsClose)
        {
            var close = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_CloseDetails, 14, 44, 44, WinoStyle.SecondaryText);
            if (close.Configuration is { } closeConfiguration)
            {
                closeConfiguration.Image = ModeBarItems.Tinted(WinoIconGlyph.Dismiss, 14, WinoStyle.SecondaryText);
                close.Configuration = closeConfiguration;
            }
            close.PrimaryActionTriggered += (_, _) => _viewModel.CloseDetailCommand.Execute(null);
            titleRow.AddArrangedSubview(close);
        }
        var headerStack = WinoLayout.Insets(WinoLayout.VStack(4, titleRow, _status), 12, 14, 12, 8);
        headerStack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(headerStack, header);
        var stroke = new WinoSeparator();
        header.AddSubview(stroke);
        NSLayoutConstraint.ActivateConstraints(
        [
            stroke.LeadingAnchor.ConstraintEqualTo(header.LeadingAnchor), stroke.TrailingAnchor.ConstraintEqualTo(header.TrailingAnchor),
            stroke.BottomAnchor.ConstraintEqualTo(header.BottomAnchor)
        ]);

        // Body: Steps, Schedule, Notes, calendar event, provenance.
        _stepsProgress.ProgressTintColor = WinoStyle.Accent;
        WinoLayout.Priorities(_stepsProgress, UILayoutConstraintAxis.Horizontal, 1, 250);
        var stepsHeader = WinoLayout.HStack(10, SectionLabel(Translator.ToDoPage_Steps), _stepsProgress, _stepsSummary);
        stepsHeader.Alignment = UIStackViewAlignment.Center;
        _steps.Alignment = UIStackViewAlignment.Fill;
        var steps = Section(stepsHeader, _steps, _addStep);

        var remind = Row(WinoIconGlyph.AlertOff, Translator.ToDoPage_RemindMe + " · " + Translator.ToDoPage_ComingSoon, WinoStyle.TertiaryText, null);
        remind.Enabled = false;
        var repeat = Row(WinoIconGlyph.ArrowRepeatAll, Translator.ToDoPage_Repeat + " · " + Translator.ToDoPage_ComingSoon, WinoStyle.TertiaryText, null);
        repeat.Enabled = false;
        var schedule = Section(SectionLabel(Translator.ToDoPage_Schedule), _myDay, _due, remind, repeat, new WinoSeparator(), _delete);

        var notesCard = WinoToDoStyle.Card(8, stroked: false);
        _notes.Font = WinoStyle.Body;
        _notes.TextContainerInset = new UIEdgeInsets(10, 8, 10, 8);
        _notes.Delegate = new NotesDelegate(this);
        WinoLayout.Fill(_notes, notesCard, 2);
        notesCard.HeightAnchor.ConstraintGreaterThanOrEqualTo(120).Active = true;
        notesCard.AddSubview(_notesPlaceholder);
        NSLayoutConstraint.ActivateConstraints(
        [
            _notesPlaceholder.LeadingAnchor.ConstraintEqualTo(notesCard.LeadingAnchor, 14),
            _notesPlaceholder.TopAnchor.ConstraintEqualTo(notesCard.TopAnchor, 12)
        ]);
        _notesPlaceholder.UserInteractionEnabled = false;
        WinoAccessibility.Label(_notes, Translator.ToDoPage_Notes);
        var notes = Section(SectionLabel(Translator.ToDoPage_Notes), notesCard);

        var body = WinoLayout.Insets(WinoLayout.VStack(18, steps, schedule, notes, _calendar, _created), 14, 14, 24, 14);
        body.Alignment = UIStackViewAlignment.Fill;
        _scroll.AddSubview(body);
        _single.AddSubview(header);
        _single.AddSubview(_scroll);
        header.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(_single.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(_single.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(_single.TrailingAnchor),
            _scroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(_single.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(_single.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(_single.KeyboardLayoutGuide.TopAnchor),
            body.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            body.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            body.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            body.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            body.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);
    }

    /// <summary>The Windows bulk-selection workspace: the count and four actions.</summary>
    private void BuildMulti()
    {
        var glyph = new WinoIconView(WinoIconGlyph.MultiSelect, 36, WinoStyle.Accent);
        _multiCount.TextAlignment = UITextAlignment.Center;
        var body = WinoStyle.Label(Translator.ToDoPage_SelectedTasksBody, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        body.TextAlignment = UITextAlignment.Center;
        var stack = WinoLayout.VStack(12, glyph, _multiCount, body,
            MultiAction(WinoIconGlyph.Checkmark, Translator.ToDoPage_CompleteSelectedTasks, WinoStyle.Accent, () => Observe(_viewModel.CompleteSelectedTasksCommand.ExecuteAsync(null))),
            MultiAction(WinoIconGlyph.ArrowReset, Translator.ToDoPage_ReopenSelectedTasks, WinoStyle.Accent, () => Observe(_viewModel.ReopenSelectedTasksCommand.ExecuteAsync(null))),
            MultiAction(WinoIconGlyph.Star, Translator.ToDoPage_MarkSelectedTasksImportant, WinoStyle.Accent, () => Observe(_viewModel.MarkSelectedTasksImportantCommand.ExecuteAsync(null))),
            MultiAction(WinoIconGlyph.Delete, Translator.ToDoPage_DeleteSelectedTasks, WinoToDoStyle.Critical, () => Observe(_viewModel.DeleteSelectedTasksCommand.ExecuteAsync(null))));
        stack.Alignment = UIStackViewAlignment.Fill;
        stack.SetCustomSpacing(20, body);
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        _multi.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(_multi.TopAnchor, 36),
            stack.CenterXAnchor.ConstraintEqualTo(_multi.CenterXAnchor),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(_multi.WidthAnchor, 1, -48),
            stack.WidthAnchor.ConstraintLessThanOrEqualTo(320)
        ]);
        var wide = stack.WidthAnchor.ConstraintEqualTo(320);
        wide.Priority = 700;
        wide.Active = true;
    }

    private UIButton MultiAction(WinoIconGlyph glyph, string title, UIColor tint, Action run)
    {
        var button = Row(glyph, title, tint, run);
        if (button.Configuration is { } configuration)
        {
            configuration.Background.BackgroundColor = WinoToDoStyle.CardFill;
            configuration.Background.StrokeColor = WinoToDoStyle.CardStroke;
            configuration.Background.StrokeWidth = 1;
            configuration.Background.CornerRadius = 8;
            button.Configuration = configuration;
        }
        _multiActions.Add(button);
        return button;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        _bindings.Own(new NestedPropertyBinding<ToDoPageViewModel, TaskItemViewModel, object>(_viewModel, nameof(ToDoPageViewModel.SelectedTask),
            static vm => vm.SelectedTask, BindTask, _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ToDoPageViewModel, int>(_viewModel, nameof(ToDoPageViewModel.SelectedTaskCount),
            static vm => vm.SelectedTaskCount, _ => ApplySurface(), _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ToDoPageViewModel, bool>(_viewModel, nameof(ToDoPageViewModel.CanEditSelectedTask),
            static vm => vm.CanEditSelectedTask, value =>
            {
                _check.Enabled = value;
                _title.Editable = value;
                _star.Enabled = value;
                _addStep.Enabled = value;
                _myDay.Enabled = value;
                _due.Enabled = value;
                _delete.Enabled = value;
                _notes.Editable = value;
            }, _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ToDoPageViewModel, bool>(_viewModel, nameof(ToDoPageViewModel.CanCreateCalendarEvent),
            static vm => vm.CanCreateCalendarEvent, value => _calendar.Hidden = !value, _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ToDoPageViewModel, string>(_viewModel, nameof(ToDoPageViewModel.SelectedTaskCountText),
            static vm => vm.SelectedTaskCountText, value => _multiCount.Text = value ?? string.Empty, _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ToDoPageViewModel, bool>(_viewModel, nameof(ToDoPageViewModel.CanEditSelectedTasks),
            static vm => vm.CanEditSelectedTasks, value => { foreach (var button in _multiActions) button.Enabled = value; }, _dispatcher, _error));
        ApplySurface();
    }

    /// <summary>Raised when the selection empties (the iPhone pops the detail).</summary>
    public event EventHandler? SelectionCleared;

    private void ApplySurface()
    {
        if (_released) return;
        int count = _viewModel.SelectedTaskCount;
        _single.Hidden = count != 1;
        _multi.Hidden = count < 2;
        _empty.Hidden = count != 0;
        if (count == 0) SelectionCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Binds the drawer to one task; the returned scope is disposed when the selection changes.</summary>
    private IDisposable BindTask(TaskItemViewModel task)
    {
        _taskScope?.Dispose();
        var scope = _taskScope = new BindingScope();
        _task = task;
        scope.Own(new ActionDisposable(() => { if (ReferenceEquals(_task, task)) _task = null; }));
        void On<TValue>(string property, Func<TaskItemViewModel, TValue> read, Action<TValue> apply)
            => scope.Own(new PropertyBinding<TaskItemViewModel, TValue>(task, property, read, apply, _dispatcher, _error));

        On(nameof(task.Title), static item => item.Title, value => { if (!_title.IsFirstResponder) _title.Text = value ?? string.Empty; });
        On(nameof(task.IsCompleted), static item => item.IsCompleted, value =>
        {
            _check.Checked = value;
            _check.Label = task.CompletionActionText;
            _title.TextColor = value ? WinoStyle.TertiaryText : WinoStyle.PrimaryText;
        });
        On(nameof(task.IsImportant), static item => item.IsImportant, value =>
        {
            if (_star.Configuration is { } configuration)
            {
                configuration.Image = ModeBarItems.Tinted(value ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, 20, value ? WinoToDoStyle.Important : WinoStyle.SecondaryText);
                _star.Configuration = configuration;
            }
            WinoAccessibility.Label(_star, task.ImportanceActionText);
        });
        On(nameof(task.DueDisplayText), static item => item.DueDisplayText, _ => ApplyStatus(task));
        On(nameof(task.IsOverdue), static item => item.IsOverdue, _ => ApplyStatus(task));
        On(nameof(task.IsInMyDay), static item => item.IsInMyDay, _ => ApplyStatus(task));
        On(nameof(task.ShowSummaryListName), static item => item.ShowSummaryListName, _ => ApplyStatus(task));
        On(nameof(task.ListName), static item => item.ListName, _ => ApplyStatus(task));
        On(nameof(task.StepSummaryText), static item => item.StepSummaryText, _ => ApplySteps(task));
        On(nameof(task.Notes), static item => item.Notes, value =>
        {
            if (_notes.IsFirstResponder) return;
            _notes.Text = value ?? string.Empty;
            _notesPlaceholder.Hidden = !string.IsNullOrEmpty(value);
        });
        On(nameof(task.CreatedOnText), static item => item.CreatedOnText, value => _created.Text = value ?? string.Empty);

        NotifyCollectionChangedEventHandler stepsChanged = (_, _) => _ = _dispatcher.ExecuteOnUIThread(() => { if (!_released && ReferenceEquals(_task, task)) RebuildSteps(task); });
        task.Steps.CollectionChanged += stepsChanged;
        scope.Own(new ActionDisposable(() => task.Steps.CollectionChanged -= stepsChanged));
        RebuildSteps(task);
        Title = task.Title;
        return scope;
    }

    private void ApplyStatus(TaskItemViewModel task)
    {
        _status.Text = TaskRowPresentation.DetailSummary(task);
        _status.TextColor = task.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.SecondaryText;
        _status.Hidden = !task.HasDetailSummary;
        SetRowTitle(_myDay, task.MyDayActionText, WinoStyle.Accent);
        SetRowTitle(_due, task.HasDueDate ? task.DueDisplayText : Translator.ToDoPage_AddDueDate,
            task.HasDueDate && task.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.Accent);
    }

    private UIMenuElement[] DueMenu()
    {
        var hasDue = _task?.HasDueDate == true;
        return
        [
            ModeBarItems.Action(Translator.ToDoPage_DuePresetToday, WinoIconGlyph.CalendarToday, () => Observe(_viewModel.SetDuePresetCommand.ExecuteAsync("today"))),
            ModeBarItems.Action(Translator.ToDoPage_DuePresetTomorrow, WinoIconGlyph.Calendar, () => Observe(_viewModel.SetDuePresetCommand.ExecuteAsync("tomorrow"))),
            ModeBarItems.Action(Translator.ToDoPage_DuePresetNextWeek, WinoIconGlyph.Calendar, () => Observe(_viewModel.SetDuePresetCommand.ExecuteAsync("nextweek"))),
            ModeBarItems.Action(Translator.ToDoPage_DuePresetPickDate + "…", WinoIconGlyph.Calendar, () => { if (_task is { } task) _pickDate(task); }),
            ModeBarItems.Inline(ModeBarItems.Action(Translator.ToDoPage_RemoveDueDate, WinoIconGlyph.Dismiss, () => Observe(_viewModel.SetDuePresetCommand.ExecuteAsync("none")), hasDue))
        ];
    }

    private void ApplySteps(TaskItemViewModel task)
    {
        bool has = task.HasSteps;
        _stepsProgress.Hidden = !has;
        _stepsSummary.Hidden = !has;
        _stepsSummary.Text = task.StepSummaryText;
        _stepsProgress.Progress = task.StepCount == 0 ? 0 : (float)task.CompletedStepCount / task.StepCount;
    }

    private void RebuildSteps(TaskItemViewModel task)
    {
        _stepsScope?.Dispose();
        var scope = _stepsScope = new BindingScope();
        foreach (var view in _steps.ArrangedSubviews) { _steps.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var step in task.Steps)
        {
            var check = new WinoRoundCheckbox(18) { Checked = step.IsCompleted, Enabled = step.IsEditable };
            check.Toggled += (_, _) => Observe(_viewModel.ToggleStepCommand.ExecuteAsync(step));
            var field = new UITextField
            {
                Text = step.Title ?? string.Empty,
                Font = WinoStyle.Body,
                Enabled = step.IsEditable,
                ReturnKeyType = UIReturnKeyType.Done,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            WinoLayout.Priorities(field, UILayoutConstraintAxis.Horizontal, 1, 250);
            WinoAccessibility.Label(field, Translator.ToDoPage_Steps);
            field.ShouldReturn = textField => { textField.ResignFirstResponder(); return true; };
            field.EditingDidEnd += (_, _) =>
            {
                var title = field.Text?.Trim() ?? string.Empty;
                if (title.Length == 0 || title == step.Title) { field.Text = step.Title ?? string.Empty; return; }
                step.Title = title;
                Observe(_viewModel.SaveStepCommand.ExecuteAsync(step));
            };
            var delete = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_DeleteStep, 12, 36, 36, WinoStyle.SecondaryText);
            if (delete.Configuration is { } deleteConfiguration)
            {
                deleteConfiguration.Image = ModeBarItems.Tinted(WinoIconGlyph.Dismiss, 12, WinoStyle.SecondaryText);
                delete.Configuration = deleteConfiguration;
            }
            delete.Enabled = step.IsEditable;
            delete.PrimaryActionTriggered += (_, _) => Observe(_viewModel.DeleteStepCommand.ExecuteAsync(step));
            var row = WinoLayout.Insets(WinoLayout.HStack(10, check, field, delete), 2, 6, 2, 0);
            row.Alignment = UIStackViewAlignment.Center;
            scope.Own(new PropertyBinding<TaskStepViewModel, string>(step, nameof(step.Title), static item => item.Title,
                value => { if (!field.IsFirstResponder) field.Text = value ?? string.Empty; }, _dispatcher, _error));
            scope.Own(new PropertyBinding<TaskStepViewModel, bool>(step, nameof(step.IsCompleted), static item => item.IsCompleted, value => check.Checked = value, _dispatcher, _error));
            _steps.AddArrangedSubview(row);
        }
        ApplySteps(task);
    }

    private void CommitTitle()
    {
        if (_task is not { } task || !_viewModel.CanEditSelectedTask) return;
        var title = _title.Text?.Trim() ?? string.Empty;
        if (title.Length == 0) { _title.Text = task.Title ?? string.Empty; return; }
        if (title != task.Title) task.Title = title;
        Observe(_viewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    private void CommitNotes()
    {
        _notesPlaceholder.Hidden = !string.IsNullOrEmpty(_notes.Text);
        if (_task is not { } task || !_viewModel.CanEditSelectedTask) return;
        var notes = _notes.Text ?? string.Empty;
        if (notes == (task.Notes ?? string.Empty)) return;
        task.Notes = notes;
        Observe(_viewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    // ---- Factories ----

    private static UILabel SectionLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        label.AccessibilityTraits = UIAccessibilityTrait.Header;
        return label;
    }

    private static UIStackView Section(params UIView[] rows)
    {
        var stack = WinoLayout.VStack(4, rows);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>A drawer action row (Windows drawer buttons): glyph and title, leading-aligned, 44pt tall.</summary>
    private static UIButton Row(WinoIconGlyph glyph, string title, UIColor tint, Action? run)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = ModeBarItems.Glyph(glyph, 18);
        configuration.ImagePadding = 12;
        configuration.Title = title;
        configuration.BaseForegroundColor = tint;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(10, 6, 10, 6);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        if (run is not null) button.PrimaryActionTriggered += (_, _) => run();
        return button;
    }

    private static void SetRowTitle(UIButton button, string title, UIColor tint)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title;
        configuration.BaseForegroundColor = tint;
        button.Configuration = configuration;
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _error(exception); }
    }

    /// <summary>Stops following the ViewModel (the page released or the drawer left its host).</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        SelectionCleared = null;
        _taskScope?.Dispose();
        _stepsScope?.Dispose();
        _bindings.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }

    private sealed class TitleDelegate(ToDoTaskDetailViewController owner) : UITextViewDelegate
    {
        public override bool ShouldChangeText(UITextView textView, Foundation.NSRange range, string text)
        {
            if (text != "\n") return true;
            textView.ResignFirstResponder();
            return false;
        }

        public override void EditingEnded(UITextView textView) => owner.CommitTitle();
    }

    private sealed class NotesDelegate(ToDoTaskDetailViewController owner) : UITextViewDelegate
    {
        public override void Changed(UITextView textView) => owner._notesPlaceholder.Hidden = !string.IsNullOrEmpty(textView.Text);

        public override void EditingEnded(UITextView textView) => owner.CommitNotes();
    }
}
