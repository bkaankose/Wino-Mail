using AppKit;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.Controls.AppKit.ToDo;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Open tasks due in the days on screen, in a row above the calendar (Windows CalendarPage due tasks strip). Ticking one
/// completes it through the task pipeline; the row leaves when the completion is applied.
/// </summary>
public sealed partial class CalendarPageViewController
{
    private const double DueStripHeight = 40;

    private NSScrollView _dueStrip = null!;
    private NSStackView _dueItems = null!;
    private NSLayoutConstraint _dueStripTop = null!;
    private NSLayoutConstraint _dueStripHeight = null!;
    private bool _dueStripReloadScheduled;

    private NSView BuildDueTasksStrip()
    {
        _dueItems = WinoLayout.HStack(6);
        _dueItems.Alignment = NSLayoutAttribute.CenterY;

        var document = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        document.AddSubview(_dueItems);
        _dueStrip = new NSScrollView
        {
            DocumentView = document,
            HasHorizontalScroller = true,
            HasVerticalScroller = false,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false,
            Hidden = true
        };
        NSLayoutConstraint.ActivateConstraints(
        [
            _dueItems.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _dueItems.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _dueItems.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _dueItems.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.HeightAnchor.ConstraintEqualTo(_dueStrip.ContentView.HeightAnchor),
            document.WidthAnchor.ConstraintGreaterThanOrEqualTo(_dueStrip.ContentView.WidthAnchor)
        ]);
        _dueStripTop = _dueStrip.TopAnchor.ConstraintEqualTo(_toolbar.BottomAnchor);
        _dueStripHeight = _dueStrip.HeightAnchor.ConstraintEqualTo(0);
        _dueStripHeight.Active = true;
        WinoAccessibility.Label(_dueStrip, Translator.Calendar_DueTasks);
        return _dueStrip;
    }

    private void BindDueTasks()
    {
        if (ViewModel.DueTasks is not { } dueTasks)
        {
            ApplyDueStrip(false);
            return;
        }

        dueTasks.Items.CollectionChanged += DueTasksChanged;
        Bindings.Own(new ActionDisposable(() => dueTasks.Items.CollectionChanged -= DueTasksChanged));
        Bindings.Own(new PropertyBinding<CalendarDueTasksViewModel, bool>(dueTasks, nameof(CalendarDueTasksViewModel.HasItems),
            static vm => vm.HasItems, ApplyDueStrip, Dispatcher, ReportError));
        ScheduleDueStripReload();
    }

    private void DueTasksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => ScheduleDueStripReload();

    private void ScheduleDueStripReload()
    {
        if (_dueStripReloadScheduled) return;
        _dueStripReloadScheduled = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _dueStripReloadScheduled = false;
            RebuildDueStrip();
        });
    }

    private void RebuildDueStrip()
    {
        if (_released || ViewModel.DueTasks is not { } dueTasks) return;

        foreach (var view in _dueItems.ArrangedSubviews)
        {
            _dueItems.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
        }
        foreach (var task in dueTasks.Items.ToArray())
            _dueItems.AddArrangedSubview(new DueTaskChip(this, task));
        ApplyDueStrip(dueTasks.HasItems);
    }

    private void ApplyDueStrip(bool hasItems)
    {
        _dueStrip.Hidden = !hasItems;
        _dueStripTop.Constant = hasItems ? 8 : 0;
        _dueStripHeight.Constant = hasItems ? (nfloat)DueStripHeight : 0;
    }

    private void CompleteDueTask(TaskItemViewModel task)
    {
        if (ViewModel.DueTasks is not { } dueTasks) return;
        _ = dueTasks.CompleteCommand.ExecuteAsync(task);
    }

    private sealed class DueTaskChip : WinoSurfaceView
    {
        public DueTaskChip(CalendarPageViewController owner, TaskItemViewModel task)
        {
            Fill = WinoToDoStyle.CardFill;
            Stroke = WinoToDoStyle.CardStroke;
            CornerRadius = 5;
            TranslatesAutoresizingMaskIntoConstraints = false;

            var check = new WinoRoundCheckbox(14) { Checked = task.IsCompleted, Enabled = task.IsEditable };
            check.Label = task.CompletionActionText;
            check.Toggled += (_, _) => owner.CompleteDueTask(task);

            var title = WinoStyle.Label(task.Title, WinoStyle.Caption, WinoStyle.PrimaryText, 1);
            title.LineBreakMode = NSLineBreakMode.TruncatingTail;
            title.PreferredMaxLayoutWidth = (nfloat)220;
            var due = WinoStyle.Label(task.DueDisplayText, NSFont.SystemFontOfSize((nfloat)11), task.IsOverdue ? WinoToDoStyle.Critical : WinoStyle.SecondaryText, 1);
            var text = WinoLayout.VStack(0, title, due);
            text.Alignment = NSLayoutAttribute.Leading;

            var row = WinoLayout.HStack(6, check, text);
            row.Alignment = NSLayoutAttribute.CenterY;
            WinoLayout.Fill(row, this, 6, 10, 6, 10);
            WinoAccessibility.Label(this, task.AutomationName);
        }
    }
}
