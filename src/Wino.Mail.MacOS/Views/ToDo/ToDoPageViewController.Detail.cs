using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

public sealed partial class ToDoPageViewController
{
    private WinoSurfaceView _drawerStroke = null!;
    private NSButton _detailBack = null!;
    private WinoRoundCheckbox _detailCheck = null!;
    private NSTextField _detailTitle = null!;
    private NSButton _detailStar = null!;
    private NSStackView _statusRow = null!;
    private NSTextField _statusDue = null!;
    private NSTextField _statusDot1 = null!;
    private NSTextField _statusMyDay = null!;
    private NSTextField _statusDot2 = null!;
    private NSTextField _statusList = null!;
    private NSProgressIndicator _stepsProgress = null!;
    private NSTextField _stepsSummary = null!;
    private NSStackView _stepsStack = null!;
    private WinoDrawerActionRow _addStepRow = null!;
    private WinoDrawerActionRow _myDayRow = null!;
    private WinoDrawerActionRow _dueRow = null!;
    private NSMenuItem _removeDueItem = null!;
    private WinoDrawerActionRow _deleteRow = null!;
    private NotesTextView _notes = null!;
    private NSTextField _notesPlaceholder = null!;
    private WinoDrawerActionRow _calendarRow = null!;
    private NSTextField _createdLabel = null!;
    private NSButton _multiBack = null!;
    private NSTextField _multiCountHeader = null!;
    private NSTextField _multiCountBody = null!;
    private readonly List<NSButton> _multiActions = new();
    private BindingScope? _taskScope;
    private BindingScope? _stepsScope;
    private bool _detailBound;

    private NSView BuildDrawerHost()
    {
        var host = new WinoSurfaceView { Fill = WinoToDoStyle.DrawerFill };
        _drawerStroke = new WinoSurfaceView { Fill = WinoToDoStyle.CardStroke };
        host.AddSubview(_drawerStroke);
        NSLayoutConstraint.ActivateConstraints(
        [
            _drawerStroke.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            _drawerStroke.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            _drawerStroke.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            _drawerStroke.WidthAnchor.ConstraintEqualTo(1)
        ]);

        _detailPane = BuildDetailPane();
        _multiPane = BuildMultiPane();
        WinoLayout.Fill(_detailPane, host, 0, 1, 0, 0);
        WinoLayout.Fill(_multiPane, host, 0, 1, 0, 0);
        _detailPane.Hidden = true;
        _multiPane.Hidden = true;
        return host;
    }

    // ---- Single task ----

    private NSView BuildDetailPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        // Pinned header (Windows padding 10,8; card fill, bottom stroke).
        var header = new WinoSurfaceView { Fill = WinoToDoStyle.CardFill };
        var headerStroke = new WinoSurfaceView { Fill = WinoToDoStyle.CardStroke };
        _detailBack = new NSButton { Title = Translator.ToDoPage_Back, Bordered = false, BezelStyle = NSBezelStyle.Inline, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _detailBack.Activated += (_, _) => ViewModel.BackToTaskListCommand.Execute(null);
        _detailCheck = new WinoRoundCheckbox();
        _detailCheck.Toggled += (_, _) => { if (ViewModel.SelectedTask is { } task) Observe(ViewModel.ToggleTaskCommand.ExecuteAsync(task)); };
        _detailTitle = new WrappingTextField
        {
            Bezeled = false, Bordered = false, DrawsBackground = false, FocusRingType = NSFocusRingType.None,
            Font = NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), UsesSingleLineMode = false, MaximumNumberOfLines = 3,
            LineBreakMode = NSLineBreakMode.ByWordWrapping, TranslatesAutoresizingMaskIntoConstraints = false
        };
        _detailTitle.Cell.Wraps = true;
        _detailTitle.Cell.Scrollable = false;
        _detailTitle.PreferredMaxLayoutWidth = 220;
        _detailTitle.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Vertical);
        _detailTitle.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Vertical);
        _detailTitle.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _detailTitle.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _detailTitle.EditingEnded += (_, _) => CommitTitle();
        WinoAccessibility.Label(_detailTitle, Translator.ToDoPage_Tasks);
        _detailStar = WinoToDoStyle.IconButton(WinoIconGlyph.Star, string.Empty, 16, 28, 28, WinoStyle.SecondaryText);
        _detailStar.Activated += (_, _) => { if (ViewModel.SelectedTask is { } task) Observe(ViewModel.ToggleImportanceCommand.ExecuteAsync(task)); };
        var close = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_CloseDetails, 14, 28, 28, WinoStyle.SecondaryText);
        close.Activated += (_, _) => ViewModel.CloseDetailCommand.Execute(null);
        var titleRow = WinoLayout.HStack(10, _detailBack, _detailCheck, _detailTitle, _detailStar, close);
        titleRow.Alignment = NSLayoutAttribute.Top;
        _detailCheck.TopAnchor.ConstraintEqualTo(titleRow.TopAnchor, 3).Active = true;

        _statusDue = WinoToDoStyle.Caption();
        _statusDot1 = WinoToDoStyle.Caption("•", WinoStyle.TertiaryText);
        _statusMyDay = WinoToDoStyle.Caption(Translator.ToDoPage_MyDay, WinoStyle.Accent);
        _statusDot2 = WinoToDoStyle.Caption("•", WinoStyle.TertiaryText);
        _statusList = WinoToDoStyle.Caption();
        _statusRow = WinoLayout.HStack(6, _statusDue, _statusDot1, _statusMyDay, _statusDot2, _statusList);
        _statusRow.EdgeInsets = new NSEdgeInsets(0, 28, 0, 0);

        var headerStack = WinoLayout.VStack(4, titleRow, _statusRow);
        headerStack.EdgeInsets = new NSEdgeInsets(10, 12, 10, 8);
        titleRow.WidthAnchor.ConstraintEqualTo(headerStack.WidthAnchor, 1, -20).Active = true;
        WinoLayout.Fill(headerStack, header);
        header.AddSubview(headerStroke);
        NSLayoutConstraint.ActivateConstraints(
        [
            headerStroke.LeadingAnchor.ConstraintEqualTo(header.LeadingAnchor),
            headerStroke.TrailingAnchor.ConstraintEqualTo(header.TrailingAnchor),
            headerStroke.BottomAnchor.ConstraintEqualTo(header.BottomAnchor),
            headerStroke.HeightAnchor.ConstraintEqualTo(1)
        ]);

        // Body: Steps, Schedule, Notes, calendar event, provenance.
        var stepsLabel = WinoDrawerSection.Label(Translator.ToDoPage_Steps);
        _stepsProgress = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, Indeterminate = false, MinValue = 0, MaxValue = 1, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _stepsProgress.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _stepsSummary = WinoToDoStyle.Caption();
        var stepsHeader = WinoLayout.HStack(10, stepsLabel, _stepsProgress, _stepsSummary);
        stepsHeader.EdgeInsets = new NSEdgeInsets(0, 12, 2, 12);
        _stepsStack = WinoLayout.VStack(2);
        _stepsStack.EdgeInsets = new NSEdgeInsets(0, 8, 0, 8);
        _addStepRow = new WinoDrawerActionRow(WinoIconGlyph.Add, Translator.ToDoPage_AddStep, WinoStyle.Accent, WinoStyle.Accent) { Plain = true, FollowsAccent = true };
        _addStepRow.Clicked += (_, _) => Observe(ViewModel.AddStepCommand.ExecuteAsync(null));
        var steps = Section(stepsHeader, _stepsStack, Indent(_addStepRow));

        var scheduleLabel = Indent(WinoDrawerSection.Label(Translator.ToDoPage_Schedule), 12);
        _myDayRow = new WinoDrawerActionRow(WinoIconGlyph.WeatherSunny, Translator.ToDoPage_AddToMyDay, WinoStyle.Accent) { FollowsAccent = true };
        _myDayRow.Clicked += (_, _) => { if (ViewModel.SelectedTask is { } task) Observe(ViewModel.ToggleMyDayCommand.ExecuteAsync(task)); };
        var dueMenu = new NSMenu { AutoEnablesItems = false };
        dueMenu.AddItem(MenuItem(Translator.ToDoPage_DuePresetToday, () => Observe(ViewModel.SetDuePresetCommand.ExecuteAsync("today"))));
        dueMenu.AddItem(MenuItem(Translator.ToDoPage_DuePresetTomorrow, () => Observe(ViewModel.SetDuePresetCommand.ExecuteAsync("tomorrow"))));
        dueMenu.AddItem(MenuItem(Translator.ToDoPage_DuePresetNextWeek, () => Observe(ViewModel.SetDuePresetCommand.ExecuteAsync("nextweek"))));
        dueMenu.AddItem(NSMenuItem.SeparatorItem);
        dueMenu.AddItem(_removeDueItem = MenuItem(Translator.ToDoPage_RemoveDueDate, () => Observe(ViewModel.SetDuePresetCommand.ExecuteAsync("none"))));
        _dueRow = new WinoDrawerActionRow(WinoIconGlyph.Calendar, Translator.ToDoPage_AddDueDate, WinoStyle.Accent) { Menu = dueMenu, FollowsAccent = true };
        var remindRow = new WinoDrawerActionRow(WinoIconGlyph.AlertOff, Translator.ToDoPage_RemindMe, trailing: Translator.ToDoPage_ComingSoon) { Enabled = false };
        var repeatRow = new WinoDrawerActionRow(WinoIconGlyph.ArrowRepeatAll, Translator.ToDoPage_Repeat, trailing: Translator.ToDoPage_ComingSoon) { Enabled = false };
        var rule = new WinoSurfaceView { Fill = WinoToDoStyle.CardStroke };
        rule.HeightAnchor.ConstraintEqualTo(1).Active = true;
        _deleteRow = new WinoDrawerActionRow(WinoIconGlyph.Delete, Translator.ToDoPage_DeleteTask, WinoToDoStyle.Critical, WinoToDoStyle.Critical);
        _deleteRow.Clicked += (_, _) => { if (ViewModel.SelectedTask is { } task) Observe(ViewModel.DeleteTaskCommand.ExecuteAsync(task)); };
        var ruleRow = Indent(rule, 12);
        var schedule = Section(scheduleLabel, Indent(_myDayRow), Indent(_dueRow), Indent(remindRow), Indent(repeatRow), ruleRow, Indent(_deleteRow));
        schedule.SetCustomSpacing(6, Indent(repeatRow));
        schedule.SetCustomSpacing(6, ruleRow);

        var notesLabel = Indent(WinoDrawerSection.Label(Translator.ToDoPage_Notes), 12);
        var notesCard = WinoToDoStyle.Card(5, stroked: false);
        _notes = new NotesTextView(CommitNotes, () => _notesPlaceholder.Hidden = _notes.Value.Length > 0);
        _notes.TextContainerInset = new CGSize(6, 8);
        var notesScroll = new NSScrollView { HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        _notes.Frame = new CGRect(0, 0, 300, 90);
        _notes.MinSize = new CGSize(0, 90);
        notesScroll.DocumentView = _notes;
        WinoLayout.Fill(notesScroll, notesCard, 2);
        notesCard.HeightAnchor.ConstraintEqualTo(120).Active = true;
        _notesPlaceholder = WinoStyle.Label(Translator.ToDoPage_AddNote, WinoStyle.Body, WinoStyle.TertiaryText);
        notesCard.AddSubview(_notesPlaceholder);
        NSLayoutConstraint.ActivateConstraints(
        [
            _notesPlaceholder.LeadingAnchor.ConstraintEqualTo(notesCard.LeadingAnchor, 12),
            _notesPlaceholder.TopAnchor.ConstraintEqualTo(notesCard.TopAnchor, 10)
        ]);
        var notes = Section(notesLabel, Indent(notesCard));

        _calendarRow = new WinoDrawerActionRow(WinoIconGlyph.Calendar, Translator.ToDoPage_CreateCalendarEvent, WinoStyle.Accent) { FollowsAccent = true };
        _calendarRow.Clicked += (_, _) => Observe(ViewModel.CreateCalendarEventCommand.ExecuteAsync(null));
        _createdLabel = WinoToDoStyle.Caption(null, WinoStyle.TertiaryText);

        var body = WinoLayout.VStack(16, steps, schedule, notes, Indent(_calendarRow), Indent(_createdLabel, 12));
        body.EdgeInsets = new NSEdgeInsets(14, 0, 14, 0);
        var flipped = new FlippedView();
        flipped.AddSubview(body);
        var scroll = new NSScrollView { DocumentView = flipped, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        NSLayoutConstraint.ActivateConstraints(
        [
            body.LeadingAnchor.ConstraintEqualTo(flipped.LeadingAnchor),
            body.TrailingAnchor.ConstraintEqualTo(flipped.TrailingAnchor),
            body.TopAnchor.ConstraintEqualTo(flipped.TopAnchor),
            body.BottomAnchor.ConstraintEqualTo(flipped.BottomAnchor),
            flipped.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor)
        ]);
        foreach (var child in body.ArrangedSubviews) child.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;

        pane.AddSubview(header);
        pane.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(pane.TopAnchor),
            header.LeadingAnchor.ConstraintEqualTo(pane.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(pane.TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(pane.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(pane.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(pane.BottomAnchor)
        ]);
        return pane;
    }

    private readonly Dictionary<NSView, NSView> _indentWrappers = new();

    /// <summary>The wrapping title needs its real width before Auto Layout can size its lines.</summary>
    private void UpdateDetailTitleWidth()
    {
        if (_detailTitle is null || _detailPane.Hidden) return;
        var width = _detailTitle.Frame.Width;
        if (width > 40 && Math.Abs(_detailTitle.PreferredMaxLayoutWidth - width) > 1)
        {
            _detailTitle.PreferredMaxLayoutWidth = width;
            _detailTitle.InvalidateIntrinsicContentSize();
        }
    }

    /// <summary>Wraps a row in horizontal margins (Windows Margin="8,0" rows and "12,0" labels).</summary>
    private NSView Indent(NSView view, double margin = 8)
    {
        if (_indentWrappers.TryGetValue(view, out var known)) return known;
        var wrapper = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, wrapper, 0, margin, 0, margin);
        _indentWrappers[view] = wrapper;
        return wrapper;
    }

    private static NSStackView Section(params NSView[] rows)
    {
        var stack = WinoLayout.VStack(2, rows);
        foreach (var row in rows) row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    private void BindDetail()
    {
        if (_detailBound) return;
        _detailBound = true;
        Bindings.Own(new NestedPropertyBinding<ToDoPageViewModel, TaskItemViewModel, object>(ViewModel, nameof(ViewModel.SelectedTask),
            static vm => vm.SelectedTask, BindTask, Dispatcher, ReportError));
        Bind(nameof(ViewModel.CanEditSelectedTask), static vm => vm.CanEditSelectedTask, value =>
        {
            _detailCheck.Enabled = value;
            _detailTitle.Editable = value;
            _detailStar.Enabled = value;
            _addStepRow.Enabled = value;
            _myDayRow.Enabled = value;
            _dueRow.Enabled = value;
            _deleteRow.Enabled = value;
            _notes.Editable = value;
        });
        Bind(nameof(ViewModel.CanCreateCalendarEvent), static vm => vm.CanCreateCalendarEvent, value => _calendarRow.Hidden = !value);
        Bind(nameof(ViewModel.SelectedTaskCountText), static vm => vm.SelectedTaskCountText, value => { _multiCountHeader.StringValue = value ?? string.Empty; _multiCountBody.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.CanEditSelectedTasks), static vm => vm.CanEditSelectedTasks, value => { foreach (var button in _multiActions) button.Enabled = value; });
    }

    private void ReleaseDetail()
    {
        if (!_detailBound) return;
        _detailBound = false;
        _taskScope?.Dispose(); _taskScope = null;
        _stepsScope?.Dispose(); _stepsScope = null;
    }

    /// <summary>Binds the drawer to one task; the returned scope is disposed when the selection changes.</summary>
    private IDisposable BindTask(TaskItemViewModel task)
    {
        _taskScope?.Dispose();
        var scope = _taskScope = new BindingScope();
        void On<TValue>(string property, Func<TaskItemViewModel, TValue> read, Action<TValue> apply)
            => scope.Own(new PropertyBinding<TaskItemViewModel, TValue>(task, property, read, apply, Dispatcher, ReportError));

        On(nameof(task.Title), static item => item.Title, value => { if (_detailTitle.CurrentEditor is null) _detailTitle.StringValue = value ?? string.Empty; });
        On(nameof(task.IsCompleted), static item => item.IsCompleted, value =>
        {
            _detailCheck.Checked = value;
            _detailTitle.TextColor = value ? WinoStyle.TertiaryText : WinoStyle.PrimaryText;
            _detailCheck.Label = task.CompletionActionText;
        });
        On(nameof(task.IsImportant), static item => item.IsImportant, value =>
        {
            WinoToDoStyle.SetMonoGlyph(_detailStar, value ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, 16, value ? WinoToDoStyle.Important : WinoStyle.SecondaryText);
            _detailStar.ToolTip = task.ImportanceActionText;
        });
        On(nameof(task.DueDisplayText), static item => item.DueDisplayText, _ => ApplyStatus(task));
        On(nameof(task.IsOverdue), static item => item.IsOverdue, _ => ApplyStatus(task));
        On(nameof(task.IsInMyDay), static item => item.IsInMyDay, _ => { ApplyStatus(task); _myDayRow.Text = task.MyDayActionText; });
        On(nameof(task.ShowSummaryListName), static item => item.ShowSummaryListName, _ => ApplyStatus(task));
        On(nameof(task.ListName), static item => item.ListName, _ => ApplyStatus(task));
        On(nameof(task.StepSummaryText), static item => item.StepSummaryText, _ => ApplySteps(task));
        On(nameof(task.Notes), static item => item.Notes, value =>
        {
            if (_notes.Window?.FirstResponder == _notes) return;
            _notes.Value = value ?? string.Empty;
            _notesPlaceholder.Hidden = !string.IsNullOrEmpty(value);
        });
        On(nameof(task.CreatedOnText), static item => item.CreatedOnText, value => _createdLabel.StringValue = value ?? string.Empty);

        NotifyCollectionChangedEventHandler stepsChanged = (_, _) => OnUI(() => { if (ViewModel.SelectedTask == task) RebuildSteps(task); });
        task.Steps.CollectionChanged += stepsChanged;
        scope.Own(new ActionDisposable(() => task.Steps.CollectionChanged -= stepsChanged));
        RebuildSteps(task);
        return scope;
    }

    private void ApplyStatus(TaskItemViewModel task)
    {
        bool due = task.HasDueDate;
        bool myDay = task.IsInMyDay;
        bool list = task.ShowSummaryListName;
        _statusDue.Hidden = !due;
        _statusDue.StringValue = task.DueDisplayText;
        _statusDue.TextColor = task.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.SecondaryText;
        _statusMyDay.Hidden = !myDay;
        _statusMyDay.TextColor = WinoStyle.Accent;
        _statusDot1.Hidden = !(due && myDay);
        _statusList.Hidden = !list;
        _statusList.StringValue = task.ListName ?? string.Empty;
        _statusDot2.Hidden = !((due || myDay) && list);
        _statusRow.Hidden = !task.HasDetailSummary;
        _dueRow.Text = due ? task.DueDisplayText : Translator.ToDoPage_AddDueDate;
        _dueRow.TextColor = due && task.IsOverdue ? WinoToDoStyle.Critical : null;
        _removeDueItem.Enabled = due;
    }

    private void ApplySteps(TaskItemViewModel task)
    {
        bool has = task.HasSteps;
        _stepsProgress.Hidden = !has;
        _stepsSummary.Hidden = !has;
        _stepsSummary.StringValue = task.StepSummaryText;
        _stepsProgress.MaxValue = Math.Max(1, task.StepCount);
        _stepsProgress.DoubleValue = task.CompletedStepCount;
    }

    private void RebuildSteps(TaskItemViewModel task)
    {
        _stepsScope?.Dispose();
        var scope = _stepsScope = new BindingScope();
        foreach (var view in _stepsStack.ArrangedSubviews.ToArray()) { _stepsStack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var step in task.Steps)
        {
            var row = new WinoStepRowView(Translator.ToDoPage_DeleteStep) { Item = step, Title = step.Title ?? string.Empty, Completed = step.IsCompleted, Editable = step.IsEditable };
            row.Toggled += (_, _) => Observe(ViewModel.ToggleStepCommand.ExecuteAsync(step));
            row.TitleCommitted += (_, _) => { if (step.Title != row.Title) { step.Title = row.Title; Observe(ViewModel.SaveStepCommand.ExecuteAsync(step)); } };
            row.DeleteRequested += (_, _) => Observe(ViewModel.DeleteStepCommand.ExecuteAsync(step));
            scope.Own(new PropertyBinding<TaskStepViewModel, string>(step, nameof(step.Title), static item => item.Title, value => row.Title = value ?? string.Empty, Dispatcher, ReportError));
            scope.Own(new PropertyBinding<TaskStepViewModel, bool>(step, nameof(step.IsCompleted), static item => item.IsCompleted, value => row.Completed = value, Dispatcher, ReportError));
            _stepsStack.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_stepsStack.WidthAnchor, 1, -16).Active = true;
        }
        ApplySteps(task);
    }

    private void CommitTitle()
    {
        if (ViewModel.SelectedTask is not { } task || !ViewModel.CanEditSelectedTask) return;
        var title = _detailTitle.StringValue.Trim();
        if (title.Length == 0) { _detailTitle.StringValue = task.Title ?? string.Empty; return; }
        if (title != task.Title) task.Title = title;
        Observe(ViewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    private void CommitNotes()
    {
        _notesPlaceholder.Hidden = !string.IsNullOrEmpty(_notes.Value);
        if (ViewModel.SelectedTask is not { } task || !ViewModel.CanEditSelectedTask) return;
        if (_notes.Value != (task.Notes ?? string.Empty)) task.Notes = _notes.Value;
        Observe(ViewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    // ---- Multiple selection (Windows: same right-side workspace as mail bulk selection) ----

    private NSView BuildMultiPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var header = new WinoSurfaceView { Fill = WinoToDoStyle.CardFill };
        _multiBack = new NSButton { Title = Translator.ToDoPage_Back, BezelStyle = NSBezelStyle.Rounded, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _multiBack.Activated += (_, _) => ViewModel.BackToTaskListCommand.Execute(null);
        _multiCountHeader = WinoStyle.Label(string.Empty, WinoStyle.Heading);
        var close = WinoToDoStyle.IconButton(WinoIconGlyph.Dismiss, Translator.ToDoPage_CloseDetails, 14, 28, 28, WinoStyle.SecondaryText);
        close.Activated += (_, _) => ViewModel.CloseDetailCommand.Execute(null);
        var headerRow = WinoLayout.HStack(8, _multiBack, _multiCountHeader, WinoLayout.Spacer(), close);
        headerRow.EdgeInsets = new NSEdgeInsets(12, 14, 10, 10);
        WinoLayout.Fill(headerRow, header);
        var stroke = new WinoSurfaceView { Fill = WinoToDoStyle.CardStroke };
        header.AddSubview(stroke);
        NSLayoutConstraint.ActivateConstraints(
        [
            stroke.LeadingAnchor.ConstraintEqualTo(header.LeadingAnchor), stroke.TrailingAnchor.ConstraintEqualTo(header.TrailingAnchor),
            stroke.BottomAnchor.ConstraintEqualTo(header.BottomAnchor), stroke.HeightAnchor.ConstraintEqualTo(1)
        ]);

        var glyph = new WinoIconView(WinoIconGlyph.MultiSelect, 36, WinoStyle.Accent);
        _multiCountBody = WinoStyle.Label(string.Empty, WinoStyle.Heading);
        _multiCountBody.Alignment = NSTextAlignment.Center;
        var bodyText = WinoStyle.Label(Translator.ToDoPage_SelectedTasksBody, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        bodyText.Alignment = NSTextAlignment.Center;
        var complete = MultiAction(WinoIconGlyph.Checkmark, Translator.ToDoPage_CompleteSelectedTasks, () => Observe(ViewModel.CompleteSelectedTasksCommand.ExecuteAsync(null)));
        var reopen = MultiAction(WinoIconGlyph.ArrowReset, Translator.ToDoPage_ReopenSelectedTasks, () => Observe(ViewModel.ReopenSelectedTasksCommand.ExecuteAsync(null)));
        var important = MultiAction(WinoIconGlyph.Star, Translator.ToDoPage_MarkSelectedTasksImportant, () => Observe(ViewModel.MarkSelectedTasksImportantCommand.ExecuteAsync(null)));
        var delete = MultiAction(WinoIconGlyph.Delete, Translator.ToDoPage_DeleteSelectedTasks, () => Observe(ViewModel.DeleteSelectedTasksCommand.ExecuteAsync(null)), WinoToDoStyle.Critical);
        var body = WinoLayout.VStack(12, glyph, _multiCountBody, bodyText, complete, reopen, important, delete);
        body.Alignment = NSLayoutAttribute.CenterX;
        body.SetCustomSpacing(20, bodyText);
        foreach (var button in _multiActions) button.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        bodyText.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;

        pane.AddSubview(header);
        pane.AddSubview(body);
        NSLayoutConstraint.ActivateConstraints(
        [
            header.TopAnchor.ConstraintEqualTo(pane.TopAnchor), header.LeadingAnchor.ConstraintEqualTo(pane.LeadingAnchor), header.TrailingAnchor.ConstraintEqualTo(pane.TrailingAnchor),
            body.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 36), body.CenterXAnchor.ConstraintEqualTo(pane.CenterXAnchor),
            body.WidthAnchor.ConstraintEqualTo(pane.WidthAnchor, 1, -48), body.WidthAnchor.ConstraintLessThanOrEqualTo(320)
        ]);
        return pane;
    }

    private NSButton MultiAction(WinoIconGlyph glyph, string title, Action action, NSColor? tint = null)
    {
        var button = new NSButton
        {
            Title = "  " + title, BezelStyle = NSBezelStyle.Rounded, Image = WinoIcons.Image(glyph, 14, tint), ImagePosition = NSCellImagePosition.ImageLeading,
            Alignment = NSTextAlignment.Left, TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.HeightAnchor.ConstraintEqualTo(32).Active = true;
        button.Activated += (_, _) => action();
        _multiActions.Add(button);
        return button;
    }

    /// <summary>Notes editor: reports edits for the placeholder and commits when focus leaves (Windows LostFocus).</summary>
    private sealed class NotesTextView : NSTextView
    {
        private readonly Action _commit;
        private readonly Action _changed;

        public NotesTextView(Action commit, Action changed)
        {
            _commit = commit;
            _changed = changed;
            DrawsBackground = false;
            RichText = false;
            Font = WinoStyle.Body;
            TextColor = WinoStyle.PrimaryText;
            FocusRingType = NSFocusRingType.None;
            VerticallyResizable = true;
            HorizontallyResizable = false;
            AutoresizingMask = NSViewResizingMask.WidthSizable;
            MaxSize = new CGSize(float.MaxValue, float.MaxValue);
            TextContainer.WidthTracksTextView = true;
        }

        public override void DidChangeText()
        {
            base.DidChangeText();
            _changed();
        }

        public override bool ResignFirstResponder()
        {
            var result = base.ResignFirstResponder();
            if (result) _commit();
            return result;
        }
    }

    /// <summary>Editable field that wraps: AppKit sizes editable fields as one line, so it measures the cell at its width.</summary>
    private sealed class WrappingTextField : NSTextField
    {
        public override CGSize IntrinsicContentSize
        {
            get
            {
                var width = Frame.Width > 40 ? Frame.Width : PreferredMaxLayoutWidth;
                if (width <= 0) return base.IntrinsicContentSize;
                var size = Cell.CellSizeForBounds(new CGRect(0, 0, width, 10000));
                return new CGSize(NoIntrinsicMetric, Math.Ceiling(size.Height));
            }
        }

        public override void SetFrameSize(CGSize newSize)
        {
            bool changed = Math.Abs(newSize.Width - Frame.Width) > 0.5;
            base.SetFrameSize(newSize);
            if (changed) InvalidateIntrinsicContentSize();
        }

        public WrappingTextField() => Changed += (_, _) => InvalidateIntrinsicContentSize();
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
