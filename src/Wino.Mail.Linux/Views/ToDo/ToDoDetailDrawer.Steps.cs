using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.ToDo;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The steps section: progress, one reused <see cref="WinoStepRow"/> per step and the add entry. Step rows follow the
/// task's step collection once per main-loop turn; a row's events resolve the step it shows at that moment.
/// </summary>
internal sealed partial class ToDoDetailDrawer
{
    private readonly Gtk.Box _steps = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.ProgressBar _stepsProgress = Gtk.ProgressBar.New();
    private readonly Gtk.Label _stepsSummary = Caption();
    private readonly Gtk.Box _stepRows = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Entry _newStep = Gtk.Entry.New();
    private readonly Gtk.Box _newStepRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly List<(WinoStepRow Row, TaskStepViewModel? Step)> _rows = [];
    private MainLoop.Coalescer? _stepsReflow;
    private BindingScope? _stepsScope;

    internal int StepRowCount => _rows.Count;

    internal Gtk.Entry NewStepEntry => _newStep;

    internal WinoStepRow? StepRow(int index) => index >= 0 && index < _rows.Count ? _rows[index].Row : null;

    private void BuildSteps()
    {
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        header.SetMarginEnd(12);
        header.Append(WinoDrawerSection.Label(Translator.ToDoPage_Steps));
        _stepsProgress.SetHexpand(true);
        _stepsProgress.SetValign(Gtk.Align.Center);
        WinoA11y.Label(_stepsProgress, Translator.ToDoPage_Steps);
        header.Append(_stepsProgress);
        header.Append(_stepsSummary);
        _steps.Append(header);
        _stepRows.SetMarginStart(8);
        _stepRows.SetMarginEnd(8);
        _steps.Append(_stepRows);

        var glyph = WinoIcons.Label(WinoIconGlyph.Add, 14);
        glyph.AddCssClass("accent");
        glyph.SetCanTarget(false);
        _newStepRow.Append(glyph);
        _newStep.SetHasFrame(false);
        _newStep.SetHexpand(true);
        _newStep.SetPlaceholderText(Translator.ToDoPage_AddStep);
        WinoA11y.Label(_newStep, Translator.ToDoPage_AddStep);
        _newStepRow.Append(_newStep);
        _newStepRow.AddCssClass(WinoCssClass.StepRow);
        _newStepRow.SetMarginStart(14);
        _newStepRow.SetMarginEnd(8);
        _steps.Append(_newStepRow);

        _stepsReflow = new MainLoop.Coalescer(RefreshSteps);
        _subscriptions.Add(GtkSignals.Connect<Gtk.Entry>(h => _newStep.OnActivate += h, h => _newStep.OnActivate -= h, (_, _) => AddStep()));
    }

    private void BindSteps(TaskItemViewModel? task)
    {
        _stepsScope?.Dispose();
        _stepsScope = null;
        if (task is null)
        {
            RefreshSteps();
            return;
        }
        var scope = _stepsScope = new BindingScope();
        var reflow = _stepsReflow;
        var dispatcher = _dispatcher;
        var observed = new List<TaskStepViewModel>();
        PropertyChangedEventHandler stepChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(() => reflow?.Request());
        void Observe()
        {
            foreach (var step in observed) step.PropertyChanged -= stepChanged;
            observed.Clear();
            foreach (var step in task.Steps)
            {
                if (step is null) continue;
                step.PropertyChanged += stepChanged;
                observed.Add(step);
            }
        }
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(() =>
        {
            Observe();
            reflow?.Request();
        });
        Observe();
        task.Steps.CollectionChanged += collectionChanged;
        scope.Own(new ActionDisposable(() =>
        {
            task.Steps.CollectionChanged -= collectionChanged;
            foreach (var step in observed) step.PropertyChanged -= stepChanged;
            observed.Clear();
        }));
        RefreshSteps();
    }

    private void ApplyStepHeader(TaskItemViewModel task, bool editable)
    {
        var available = _viewModel.AreStepsAvailableForSelectedTask;
        _steps.SetVisible(available);
        _stepsProgress.SetVisible(task.HasSteps);
        _stepsProgress.SetFraction(task.StepCount == 0 ? 0 : (double)task.CompletedStepCount / task.StepCount);
        _stepsSummary.SetText(task.HasSteps ? task.StepSummaryText ?? string.Empty : string.Empty);
        _newStepRow.SetVisible(editable);
    }

    /// <summary>Reuses rows by index, disposes the extras and adds what is missing.</summary>
    private void RefreshSteps()
    {
        if (_disposed) return;
        TaskStepViewModel[] steps = _task is { } task ? [.. task.Steps.Where(static step => step is not null)] : [];
        var editable = _task is { IsEditable: true } && _viewModel.CanEditSelectedTask;
        while (_rows.Count > steps.Length)
        {
            var last = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            _stepRows.Remove(last.Row.Root);
            last.Row.Dispose();
        }
        for (var index = 0; index < steps.Length; index++)
        {
            if (index == _rows.Count) _rows.Add((CreateStepRow(), null));
            var row = _rows[index].Row;
            var step = steps[index];
            _rows[index] = (row, step);
            row.Title = step.Title ?? string.Empty;
            row.Completed = step.IsCompleted;
            row.Editable = editable && step.IsEditable;
        }
        if (_task is { } current)
        {
            _stepsProgress.SetVisible(current.HasSteps);
            _stepsProgress.SetFraction(current.StepCount == 0 ? 0 : (double)current.CompletedStepCount / current.StepCount);
            _stepsSummary.SetText(current.HasSteps ? current.StepSummaryText ?? string.Empty : string.Empty);
        }
    }

    private WinoStepRow CreateStepRow()
    {
        var row = new WinoStepRow(Translator.ToDoPage_DeleteStep, Translator.ToDoPage_ToggleCompleted);
        row.Toggled += () =>
        {
            if (StepOf(row) is { } step) _observe(_viewModel.ToggleStepCommand.ExecuteAsync(step));
        };
        row.Renamed += title =>
        {
            if (StepOf(row) is not { } step) return;
            step.Title = title;
            _observe(_viewModel.SaveStepCommand.ExecuteAsync(step));
        };
        row.DeleteRequested += () =>
        {
            if (StepOf(row) is { } step) _observe(_viewModel.DeleteStepCommand.ExecuteAsync(step));
        };
        _stepRows.Append(row.Root);
        return row;
    }

    private TaskStepViewModel? StepOf(WinoStepRow row)
    {
        foreach (var (candidate, step) in _rows)
            if (ReferenceEquals(candidate, row)) return step;
        return null;
    }

    /// <summary>Windows AddStepButton_Click: the step is named before it is created, so the server gets one write.</summary>
    private void AddStep()
    {
        if (_disposed || _task is not { IsEditable: true } task) return;
        var text = _newStep.GetText().Trim();
        _newStep.SetText(string.Empty);
        if (text.Length == 0) return;
        _observe(_viewModel.AddNamedStepAsync(task, text));
    }

    private void CommitSteps()
    {
        foreach (var (row, _) in _rows) row.Commit();
    }

    private void DisposeSteps()
    {
        _stepsScope?.Dispose();
        _stepsScope = null;
        _stepsReflow?.Dispose();
        foreach (var (row, _) in _rows) row.Dispose();
        _rows.Clear();
    }

#if DEBUG
    private string DescribeSteps()
    {
        var text = new StringBuilder();
        text.Append("visible=").Append(_steps.GetVisible()).Append(" summary='").Append(_stepsSummary.GetText()).Append("' add=").Append(_newStepRow.GetVisible());
        foreach (var (row, _) in _rows)
            text.Append(" [").Append(row.Completed ? 'x' : ' ').Append("] '").Append(row.Title).Append('\'').Append(row.Editable ? string.Empty : " ro");
        return text.ToString();
    }

    internal void DebugAddStep(string title)
    {
        _newStep.SetText(title);
        AddStep();
    }
#endif
}
