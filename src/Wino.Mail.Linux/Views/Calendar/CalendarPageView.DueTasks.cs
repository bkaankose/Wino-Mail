using System.Collections.Specialized;
using Wino.Core.Domain;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Open tasks due in the days on screen, in a row above the surface (Windows CalendarPage due tasks strip, macOS
/// CalendarPageViewController.DueTasks). Ticking one completes it through the task pipeline; its chip leaves when the
/// completion is applied.
/// </summary>
internal sealed partial class CalendarPageView
{
    private readonly Gtk.ScrolledWindow _dueStrip = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _dueItems = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
    private readonly List<DueTaskChip> _dueChips = [];
    private MainLoop.Coalescer? _dueRebuild;

    partial void BuildDueTasks(Gtk.Box page)
    {
        _dueStrip.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        _dueStrip.SetPropagateNaturalHeight(true);
        _dueStrip.SetChild(_dueItems);
        _dueStrip.SetMarginTop(8);
        _dueStrip.SetMarginStart(12);
        _dueStrip.SetMarginEnd(12);
        _dueStrip.SetVisible(false);
        _dueStrip.AddCssClass("wino-due-strip");
        WinoA11y.Label(_dueStrip, Translator.Calendar_DueTasks);
        page.Append(_dueStrip);
        _activated.Add(BindDueTasks);
    }

    private void BindDueTasks()
    {
        if (ViewModel.DueTasks is not { } dueTasks) return;
        var rebuild = new MainLoop.Coalescer(RebuildDueStrip);
        _dueRebuild = rebuild;
        Bindings.Own(rebuild);
        Bindings.Own(new ActionDisposable(ClearDueChips));
        NotifyCollectionChangedEventHandler changed = (_, _) => _ = Dispatcher.ExecuteOnUIThread(rebuild.Request);
        dueTasks.Items.CollectionChanged += changed;
        Bindings.Own(new ActionDisposable(() => dueTasks.Items.CollectionChanged -= changed));
        Bindings.Own(new PropertyBinding<CalendarDueTasksViewModel, bool>(dueTasks, nameof(CalendarDueTasksViewModel.HasItems),
            static vm => vm.HasItems, _ => rebuild.Request(), Dispatcher, ReportError));
        rebuild.Request();
    }

    private void RebuildDueStrip()
    {
        if (IsReleased || ViewModel.DueTasks is not { } dueTasks) return;
        ClearDueChips();
        foreach (var task in dueTasks.Items.ToArray())
        {
            var chip = new DueTaskChip(task, CompleteDueTask);
            _dueChips.Add(chip);
            _dueItems.Append(chip.Root);
        }
        _dueStrip.SetVisible(dueTasks.HasItems && _dueChips.Count > 0);
    }

    private void ClearDueChips()
    {
        foreach (var chip in _dueChips)
        {
            _dueItems.Remove(chip.Root);
            chip.Dispose();
        }
        _dueChips.Clear();
    }

    private void CompleteDueTask(TaskItemViewModel task)
    {
        if (ViewModel.DueTasks is not { } dueTasks) return;
        Observe(dueTasks.CompleteCommand.ExecuteAsync(task));
    }

#if DEBUG
    internal string DebugDescribeDueTasks()
    {
        var text = new System.Text.StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"visible={_dueStrip.GetVisible()} count={_dueChips.Count} viewModel={ViewModel.DueTasks?.Items.Count.ToString() ?? "-"} height={_dueStrip.GetHeight()}").AppendLine();
        foreach (var chip in _dueChips)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{chip.Task.Title}|{chip.Task.DueDisplayText}|overdue={chip.Task.IsOverdue}|editable={chip.Task.IsEditable}").AppendLine();
        return text.ToString();
    }

    internal string DebugCompleteDueTask(int index)
    {
        if (index < 0 || index >= _dueChips.Count) return "no chip";
        CompleteDueTask(_dueChips[index].Task);
        return "ok";
    }
#endif

    /// <summary>One due task: the round check box, then the title over the due text (overdue in the error colour).</summary>
    private sealed class DueTaskChip : IDisposable
    {
        private readonly WinoRoundCheckbox _check = new();

        public DueTaskChip(TaskItemViewModel task, Action<TaskItemViewModel> complete)
        {
            Task = task;
            _check.Checked = task.IsCompleted;
            _check.Enabled = task.IsEditable;
            _check.Label = task.CompletionActionText;
            _check.Toggled += () => complete(task);

            var title = Gtk.Label.New(task.Title);
            title.SetXalign(0);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            title.SetMaxWidthChars(28);
            title.SetWidthChars(Math.Min(task.Title.Length, 28));
            title.AddCssClass("caption");
            var due = Gtk.Label.New(task.DueDisplayText);
            due.SetXalign(0);
            due.AddCssClass("caption");
            due.AddCssClass(task.IsOverdue ? "error" : "dim-label");
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            text.SetValign(Gtk.Align.Center);
            text.Append(title);
            text.Append(due);

            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            row.AddCssClass("wino-due-chip");
            row.Append(_check.Root);
            row.Append(text);
            WinoA11y.Label(text, task.AutomationName);
            Root = row;
        }

        public TaskItemViewModel Task { get; }

        public Gtk.Box Root { get; }

        public void Dispose() => _check.Dispose();
    }
}
