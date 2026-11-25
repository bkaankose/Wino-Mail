using System.ComponentModel;
using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The task drawer (Windows ToDoPage detail pane, macOS ToDoPageViewController.Detail): one selected task with its
/// header, steps, schedule, notes and calendar event row, or the bulk panel when several tasks are selected. Title,
/// notes and step titles are saved when the edit is committed (Enter or focus leave), never per keystroke; a pending
/// edit is committed to the task it was typed for before the drawer shows another task.
/// </summary>
internal sealed partial class ToDoDetailDrawer : IDisposable
{
    private const string EmptyPage = "empty";
    private const string TaskPage = "task";
    private const string MultiPage = "multi";

    private readonly ToDoPageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly Action<Task> _observe;
    private readonly Action<Exception> _error;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Gtk.Button _back = Gtk.Button.NewWithLabel(Translator.ToDoPage_Back);
    private readonly WinoRoundCheckbox _check = new();
    private readonly Gtk.Entry _title = Gtk.Entry.New();
    private readonly Gtk.Button _star;
    private readonly Gtk.Button _close;
    private readonly Gtk.Box _status = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly Gtk.Label _statusDue = Caption();
    private readonly Gtk.Label _statusDot1 = Caption("•");
    private readonly Gtk.Label _statusMyDay = Caption(Translator.ToDoPage_MyDay);
    private readonly Gtk.Label _statusDot2 = Caption("•");
    private readonly Gtk.Label _statusList = Caption();
    private readonly WinoDrawerActionRow _myDay = new(WinoIconGlyph.WeatherSunny, Translator.ToDoPage_AddToMyDay);
    private readonly WinoDrawerActionRow _due = new(WinoIconGlyph.Calendar, Translator.ToDoPage_AddDueDate, hasMenu: true);
    private readonly WinoDrawerActionRow _remind = new(WinoIconGlyph.Alert, Translator.ToDoPage_RemindMe, hasMenu: true);
    private readonly WinoDrawerActionRow _repeat = new(WinoIconGlyph.ArrowRepeatAll, Translator.ToDoPage_Repeat, hasMenu: true);
    private readonly WinoDrawerActionRow _priority = new(WinoIconGlyph.Important, Translator.ToDoPage_Priority, hasMenu: true);
    private readonly WinoDrawerActionRow _categories = new(WinoIconGlyph.Tag, Translator.ToDoPage_Categories, hasMenu: true);
    private readonly WinoDrawerActionRow _delete = new(WinoIconGlyph.Delete, Translator.ToDoPage_DeleteTask, destructive: true);
    private readonly WinoDrawerActionRow _calendar = new(WinoIconGlyph.Calendar, Translator.ToDoPage_ScheduleTime);
    private readonly Gtk.TextView _notes = Gtk.TextView.New();
    private readonly Gtk.Label _notesPlaceholder = Gtk.Label.New(Translator.ToDoPage_AddNote);
    private readonly Gtk.Label _created = Caption();
    private readonly ToDoSelectionBar _multi;
    private readonly MainLoop.Coalescer _apply;
    private BindingScope? _taskScope;
    private TaskItemViewModel? _task;
    private bool _applying;
    private bool _titleDirty;
    private bool _titleFocused;
    private bool _notesDirty;
    private bool _notesFocused;
    private bool _disposed;

    public ToDoDetailDrawer(ToDoPageViewModel viewModel, IDispatcher dispatcher, Action<Task> observe, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _observe = observe;
        _error = error;
        _apply = new MainLoop.Coalescer(ApplyTask);
        _star = WinoIcons.Button(WinoIconGlyph.Star, Translator.ToDoPage_Important, 16);
        _close = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.ToDoPage_CloseDetails, 13);
        _multi = new ToDoSelectionBar(viewModel, observe);

        _stack.SetTransitionType(Gtk.StackTransitionType.None);
        _stack.SetVexpand(true);
        _stack.SetHexpand(true);
        _stack.AddNamed(Adw.Bin.New(), EmptyPage);
        _stack.AddNamed(BuildTaskPage(), TaskPage);
        _stack.AddNamed(_multi.Root, MultiPage);
        _stack.SetVisibleChildName(EmptyPage);
        BuildSteps();
        Connect();
    }

    public Gtk.Widget Root => _stack;

    public string Page => _stack.GetVisibleChildName() ?? EmptyPage;

    public TaskItemViewModel? Task => _task;

    internal Gtk.Entry TitleEntry => _title;

    internal Gtk.TextView Notes => _notes;

    internal ToDoSelectionBar Multi => _multi;

    internal WinoDrawerActionRow DueRow => _due;

    public void SetCompact(bool compact)
    {
        _back.SetVisible(compact);
        _close.SetVisible(!compact);
        _multi.SetCompact(compact);
    }

    /// <summary>Shows the page for the current selection and binds the selected task.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var task = _viewModel.IsMultipleTaskSelection ? null : _viewModel.SelectedTask;
        if (!ReferenceEquals(task, _task)) Rebind(task);
        _stack.SetVisibleChildName(_viewModel.IsMultipleTaskSelection ? MultiPage : task is not null ? TaskPage : EmptyPage);
        _multi.Refresh();
        ApplyTask();
    }

    /// <summary>Saves an edit still open in the title, the notes or a step entry.</summary>
    public void CommitPending()
    {
        CommitTitle();
        CommitNotes();
        CommitSteps();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CommitPending();
        _disposed = true;
        _taskScope?.Dispose();
        _taskScope = null;
        _task = null;
        _apply.Dispose();
        DisposeSteps();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _check.Dispose();
        foreach (var row in new[] { _myDay, _due, _remind, _repeat, _priority, _categories, _delete, _calendar }) row.Dispose();
        DisposeFields();
        _multi.Dispose();
    }

    private Gtk.Widget BuildTaskPage()
    {
        var header = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        header.AddCssClass(WinoCssClass.DrawerHeader);
        var titleRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        _back.AddCssClass("flat");
        _back.SetValign(Gtk.Align.Center);
        _back.SetVisible(false);
        WinoA11y.Rename(_back, Translator.ToDoPage_Back);
        titleRow.Append(_back);
        _check.Label = Translator.ToDoPage_ToggleCompleted;
        titleRow.Append(_check.Root);
        _title.SetHasFrame(false);
        _title.SetHexpand(true);
        WinoA11y.Label(_title, Translator.ToDoPage_Tasks);
        titleRow.Append(_title);
        _star.SetSizeRequest(28, 28);
        _star.SetValign(Gtk.Align.Center);
        _star.AddCssClass(WinoCssClass.TaskStar);
        titleRow.Append(_star);
        _close.SetValign(Gtk.Align.Center);
        titleRow.Append(_close);
        header.Append(titleRow);
        _statusDue.AddCssClass("error");
        _statusMyDay.AddCssClass("accent");
        foreach (var label in new[] { _statusDue, _statusDot1, _statusMyDay, _statusDot2, _statusList }) _status.Append(label);
        _status.SetMarginStart(36);
        header.Append(_status);

        var rule = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        rule.SetMarginStart(12);
        rule.SetMarginEnd(12);
        rule.SetMarginTop(4);
        rule.SetMarginBottom(4);
        var schedule = WinoDrawerSection.Create(WinoDrawerSection.Label(Translator.ToDoPage_Schedule),
            Indent(_myDay.Root), Indent(_due.Root), Indent(_remind.Root), Indent(_repeat.Root), Indent(_priority.Root), Indent(_categories.Root), rule, Indent(_delete.Root));

        _notes.SetWrapMode(Gtk.WrapMode.WordChar);
        _notes.SetTopMargin(8);
        _notes.SetBottomMargin(8);
        _notes.SetLeftMargin(10);
        _notes.SetRightMargin(10);
        _notes.SetAcceptsTab(false);
        WinoA11y.Label(_notes, Translator.ToDoPage_Notes);
        var notesScroll = Gtk.ScrolledWindow.New();
        notesScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        notesScroll.SetChild(_notes);
        notesScroll.SetSizeRequest(-1, 120);
        _notesPlaceholder.AddCssClass("dim-label");
        _notesPlaceholder.SetXalign(0);
        _notesPlaceholder.SetValign(Gtk.Align.Start);
        _notesPlaceholder.SetHalign(Gtk.Align.Start);
        _notesPlaceholder.SetMarginStart(12);
        _notesPlaceholder.SetMarginTop(8);
        _notesPlaceholder.SetCanTarget(false);
        var notesCard = Gtk.Overlay.New();
        notesCard.AddCssClass(WinoCssClass.DrawerNotes);
        notesCard.SetOverflow(Gtk.Overflow.Hidden);
        notesCard.SetChild(notesScroll);
        notesCard.AddOverlay(_notesPlaceholder);
        var notes = WinoDrawerSection.Create(WinoDrawerSection.Label(Translator.ToDoPage_Notes), Indent(notesCard, 12));

        _created.SetMarginStart(12);
        _created.SetMarginEnd(12);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 16);
        body.SetMarginTop(14);
        body.SetMarginBottom(14);
        body.Append(_steps);
        body.Append(schedule);
        body.Append(notes);
        body.Append(BuildLinks());
        body.Append(Indent(_calendar.Root));
        body.Append(_created);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(body);

        var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        page.Append(header);
        page.Append(scroll);
        return page;
    }

    private void Connect()
    {
        _subscriptions.Add(GtkSignals.OnClicked(_back, () => _viewModel.BackToTaskListCommand.Execute(null)));
        _subscriptions.Add(GtkSignals.OnClicked(_close, () => _viewModel.CloseDetailCommand.Execute(null)));
        _check.Toggled += () => RunOnTask(task => _viewModel.ToggleTaskCommand.ExecuteAsync(task));
        _subscriptions.Add(GtkSignals.OnClicked(_star, () => RunOnTask(task => _viewModel.ToggleImportanceCommand.ExecuteAsync(task))));
        _myDay.Clicked += () => RunOnTask(task => _viewModel.ToggleMyDayCommand.ExecuteAsync(task));
        _due.Clicked += ShowDueMenu;
        _delete.Clicked += () => RunOnTask(task => _viewModel.DeleteTaskCommand.ExecuteAsync(task));
        _calendar.Clicked += () => _observe(_viewModel.CreateCalendarEventCommand.ExecuteAsync(null));
        ConnectFieldRows();

        _subscriptions.Add(GtkSignals.OnChanged(_title, () =>
        {
            if (!_applying) _titleDirty = true;
        }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Entry>(h => _title.OnActivate += h, h => _title.OnActivate -= h, (_, _) => CommitTitle()));
        var titleFocus = Gtk.EventControllerFocus.New();
        _title.AddController(titleFocus);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => titleFocus.OnEnter += h, h => titleFocus.OnEnter -= h, (_, _) => _titleFocused = true));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => titleFocus.OnLeave += h, h => titleFocus.OnLeave -= h, (_, _) =>
        {
            _titleFocused = false;
            CommitTitle();
        }));

        var buffer = _notes.GetBuffer();
        _subscriptions.Add(GtkSignals.Connect<Gtk.TextBuffer>(h => buffer.OnChanged += h, h => buffer.OnChanged -= h, (_, _) =>
        {
            _notesPlaceholder.SetVisible(string.IsNullOrEmpty(buffer.Text));
            if (!_applying) _notesDirty = true;
        }));
        var notesFocus = Gtk.EventControllerFocus.New();
        _notes.AddController(notesFocus);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => notesFocus.OnEnter += h, h => notesFocus.OnEnter -= h, (_, _) => _notesFocused = true));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerFocus>(h => notesFocus.OnLeave += h, h => notesFocus.OnLeave -= h, (_, _) =>
        {
            _notesFocused = false;
            CommitNotes();
        }));
    }

    private void Rebind(TaskItemViewModel? task)
    {
        CommitPending();
        _taskScope?.Dispose();
        _taskScope = null;
        _task = task;
        _titleDirty = false;
        _notesDirty = false;
        if (task is null)
        {
            BindSteps(null);
            return;
        }
        var scope = _taskScope = new BindingScope();
        var apply = _apply;
        var dispatcher = _dispatcher;
        PropertyChangedEventHandler changed = (_, _) => _ = dispatcher.ExecuteOnUIThread(apply.Request);
        task.PropertyChanged += changed;
        scope.Own(new ActionDisposable(() => task.PropertyChanged -= changed));
        ApplyNotes(task, force: true);
        BindSteps(task);
    }

    private void ApplyTask()
    {
        if (_disposed || _task is not { Task: not null } task) return;
        var editable = _viewModel.CanEditSelectedTask && task.IsEditable;
        _applying = true;
        try
        {
            _check.Checked = task.IsCompleted;
            _check.Enabled = editable;
            _check.Label = task.CompletionActionText;
            if (!_titleFocused && !_titleDirty && !string.Equals(_title.GetText(), task.Title ?? string.Empty, StringComparison.Ordinal))
                _title.SetText(task.Title ?? string.Empty);
            _title.SetEditable(editable);
            if (task.IsImportant) _star.AddCssClass(WinoCssClass.ImportantOn);
            else _star.RemoveCssClass(WinoCssClass.ImportantOn);
            WinoA11y.LabelWithTooltip(_star, task.ImportanceActionText ?? Translator.ToDoPage_Important);
            WinoA11y.Checked(_star, task.IsImportant);
            _star.SetSensitive(editable);

            _statusDue.SetText(task.DueDisplayText ?? string.Empty);
            _statusDue.SetVisible(task.HasDueDate);
            if (task.IsOverdue) _statusDue.AddCssClass("error");
            else _statusDue.RemoveCssClass("error");
            _statusMyDay.SetVisible(task.IsInMyDay);
            _statusDot1.SetVisible(task.ShowMyDaySummarySeparator);
            _statusList.SetText(task.ListName ?? string.Empty);
            _statusList.SetVisible(task.ShowSummaryListName);
            _statusDot2.SetVisible(task.ShowListSummarySeparator);
            _status.SetVisible(task.HasDetailSummary);

            _myDay.Text = task.MyDayActionText ?? Translator.ToDoPage_AddToMyDay;
            _myDay.Accent = task.IsInMyDay;
            _myDay.Sensitive = editable;
            _due.Text = task.HasDueDate ? task.DueDisplayText ?? string.Empty : Translator.ToDoPage_AddDueDate;
            _due.Accent = task.HasDueDate && !task.IsOverdue;
            _due.Overdue = task.IsOverdue;
            _due.Sensitive = editable;
            _delete.Sensitive = editable;
            ApplyFieldRows(task, editable);
            _notes.SetEditable(editable);
            _calendar.Visible = _viewModel.CanCreateCalendarEvent;
            _created.SetText(task.CreatedOnText ?? string.Empty);
            ApplyNotes(task, force: false);
            ApplyStepHeader(task, editable);
        }
        finally { _applying = false; }
        RefreshSteps();
    }

    private void ApplyNotes(TaskItemViewModel task, bool force)
    {
        if (!force && (_notesDirty || _notesFocused)) return;
        var text = task.Notes ?? string.Empty;
        var buffer = _notes.GetBuffer();
        if (string.Equals(buffer.Text ?? string.Empty, text, StringComparison.Ordinal)) return;
        var applying = _applying;
        _applying = true;
        try { buffer.SetText(text, -1); }
        finally { _applying = applying; }
        _notesPlaceholder.SetVisible(text.Length == 0);
    }

    private void CommitTitle()
    {
        if (!_titleDirty || _task is not { Task: not null } task) return;
        _titleDirty = false;
        var text = _title.GetText().Trim();
        if (text.Length == 0 || !task.IsEditable)
        {
            _applying = true;
            try { _title.SetText(task.Title ?? string.Empty); }
            finally { _applying = false; }
            return;
        }
        if (string.Equals(text, task.Title, StringComparison.Ordinal)) return;
        task.Title = text;
        _observe(_viewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    private void CommitNotes()
    {
        if (!_notesDirty || _task is not { Task: not null } task) return;
        _notesDirty = false;
        if (!task.IsEditable) return;
        var text = _notes.GetBuffer().Text ?? string.Empty;
        if (string.Equals(text, task.Notes ?? string.Empty, StringComparison.Ordinal)) return;
        task.Notes = text;
        _observe(_viewModel.SaveTaskCommand.ExecuteAsync(task));
    }

    private void ShowDueMenu()
    {
        if (_disposed || _task is not { IsEditable: true } task) return;
        var menu = WinoMenus.Create();
        var viewModel = _viewModel;
        var observe = _observe;
        void Preset(string label, string preset, WinoIconGlyph glyph)
            => menu.Item(label, () => observe(viewModel.SetDuePresetCommand.ExecuteAsync(preset)), icon: glyph);
        Preset(Translator.ToDoPage_DuePresetToday, "today", WinoIconGlyph.CalendarToday);
        Preset(Translator.ToDoPage_DuePresetTomorrow, "tomorrow", WinoIconGlyph.Calendar);
        Preset(Translator.ToDoPage_DuePresetNextWeek, "nextweek", WinoIconGlyph.Calendar);
        menu.Separator();
        menu.Item(Translator.ToDoPage_RemoveDueDate, () => observe(viewModel.SetDuePresetCommand.ExecuteAsync("none")), task.HasDueDate, icon: WinoIconGlyph.Dismiss);
        ContextMenu.Show(_due.Root, menu.Build());
    }

    private void RunOnTask(Func<TaskItemViewModel, Task> action)
    {
        if (_disposed || _task is not { } task) return;
        _observe(action(task));
    }

    private static Gtk.Widget Indent(Gtk.Widget widget, int margin = 8)
    {
        widget.SetMarginStart(margin);
        widget.SetMarginEnd(margin);
        return widget;
    }

    private static Gtk.Label Caption(string? text = null)
    {
        var label = Gtk.Label.New(text ?? string.Empty);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        label.SetXalign(0);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        return label;
    }

#if DEBUG
    internal string Describe()
    {
        var text = new StringBuilder();
        text.Append("page=").Append(Page);
        if (Page == MultiPage) return text.Append(' ').Append(_multi.Describe()).ToString();
        if (_task is null) return text.ToString();
        text.Append(" title='").Append(_title.GetText()).Append("' editable=").Append(_title.GetEditable())
            .Append(" check=").Append(_check.Checked).Append('/').Append(_check.Enabled)
            .Append(" star=").Append(_star.HasCssClass(WinoCssClass.ImportantOn)).Append('/').Append(_star.GetSensitive())
            .Append(" back=").Append(_back.GetVisible()).Append(" close=").Append(_close.GetVisible())
            .Append(" | status: due='").Append(_statusDue.GetVisible() ? _statusDue.GetText() : string.Empty)
            .Append("' myday=").Append(_statusMyDay.GetVisible()).Append(" list='").Append(_statusList.GetVisible() ? _statusList.GetText() : string.Empty).Append('\'');
        text.Append(" | steps: ").Append(DescribeSteps());
        foreach (var row in new[] { _myDay, _due, _remind, _repeat, _priority, _categories, _delete, _calendar })
            text.Append(" | row '").Append(row.Text).Append('\'')
                .Append(row.Trailing is { } trailing ? " trailing='" + trailing + "'" : string.Empty)
                .Append(" sensitive=").Append(row.Sensitive).Append(" visible=").Append(row.Visible)
                .Append(row.Accent ? " accent" : string.Empty).Append(row.Overdue ? " overdue" : string.Empty);
        text.Append(" | notes='").Append(_notes.GetBuffer().Text).Append("' editable=").Append(_notes.GetEditable())
            .Append(" placeholder=").Append(_notesPlaceholder.GetVisible())
            .Append(" | links=").Append(_linksSection.GetVisible()).Append(':').Append(_links?.Describe())
            .Append(" | created='").Append(_created.GetText()).Append('\'');
        return text.ToString();
    }

    internal void DebugSetTitle(string text)
    {
        _title.GrabFocus();
        _title.SetText(text);
        CommitTitle();
    }

    internal void DebugSetNotes(string text)
    {
        _notes.GetBuffer().SetText(text, -1);
        CommitNotes();
    }

    internal Gtk.Widget DebugDueAnchor => _due.Root;

    internal void DebugShowDueMenu() => ShowDueMenu();
#endif
}
