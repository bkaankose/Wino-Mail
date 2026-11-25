using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.ToDo;

/// <summary>
/// The notice and undo bars above quick add (Windows ToDoPage notice and undo InfoBars, macOS
/// ToDoPageViewController.Bars). The notice reports copy, import and export results; the undo bar holds a complete,
/// delete or move until it closes or Undo is pressed.
/// </summary>
internal sealed partial class ToDoPageView
{
    private readonly Gtk.Box _bars = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private WinoInfoBar? _noticeBar;
    private WinoInfoBar? _undoBar;
    private Gtk.Box? _barsHome;
    private Gtk.Widget? _barsAfter;
    private bool _barsFloating;

    private Gtk.Widget BuildBars()
    {
        var self = _self;
        var notice = _noticeBar = Bindings.Own(new WinoInfoBar { IsClosable = true, IsOpen = false });
        notice.Closed += (_, _) => self.Value?.ViewModel.CloseNoticeCommand.Execute(null);

        var undo = _undoBar = Bindings.Own(new WinoInfoBar(WinoInfoBarSeverity.Informational) { IsClosable = true, ActionTitle = Translator.UndoActions_Button, IsOpen = false });
        undo.ActionInvoked += _ =>
        {
            if (self.Value is { } view) view.Observe(view.ViewModel.UndoTaskActionCommand.ExecuteAsync(null));
        };
        undo.Closed += (_, _) => self.Value?.ViewModel.CloseUndoBarCommand.Execute(null);

        _bars.SetMarginStart(20);
        _bars.SetMarginEnd(20);
        _bars.SetMarginTop(4);
        _bars.SetVisible(false);
        _bars.Append(notice.Root);
        _bars.Append(undo.Root);
        return _bars;
    }

    private void BindBars()
    {
        Bind.Bind(nameof(ToDoPageViewModel.IsNoticeOpen), static vm => vm.IsNoticeOpen, _ => ApplyNotice());
        Bind.Bind(nameof(ToDoPageViewModel.NoticeTitle), static vm => vm.NoticeTitle, _ => ApplyNotice());
        Bind.Bind(nameof(ToDoPageViewModel.NoticeMessage), static vm => vm.NoticeMessage, _ => ApplyNotice());
        Bind.Bind(nameof(ToDoPageViewModel.NoticeSeverity), static vm => vm.NoticeSeverity, _ => ApplyNotice());
        Bind.Bind(nameof(ToDoPageViewModel.IsUndoBarOpen), static vm => vm.IsUndoBarOpen, _ => ApplyUndo());
        Bind.Bind(nameof(ToDoPageViewModel.UndoBarTitle), static vm => vm.UndoBarTitle, _ => ApplyUndo());
    }

    private void ApplyNotice()
    {
        if (IsReleased || _noticeBar is not { } bar) return;
        bar.Title = ViewModel.NoticeTitle;
        bar.Message = ViewModel.NoticeMessage;
        bar.Severity = MapSeverity(ViewModel.NoticeSeverity);
        if (bar.IsOpen != ViewModel.IsNoticeOpen) bar.IsOpen = ViewModel.IsNoticeOpen;
        ApplyBarsVisibility();
    }

    private void ApplyUndo()
    {
        if (IsReleased || _undoBar is not { } bar) return;
        bar.Title = ViewModel.UndoBarTitle;
        if (bar.IsOpen != ViewModel.IsUndoBarOpen) bar.IsOpen = ViewModel.IsUndoBarOpen;
        ApplyBarsVisibility();
    }

    /// <summary>
    /// The bars belong above quick add, but a compact page that shows only the drawer hides the list column; the bars
    /// then float at the bottom of the page so an undo stays reachable for its whole window.
    /// </summary>
    private void PlaceBars()
    {
        if (IsReleased || _barsHome is not { } home || _barsAfter is not { } after) return;
        var floating = _layout.State == EndPaneState.Swapped && _layout.ShowsEnd;
        if (floating == _barsFloating) return;
        _barsFloating = floating;
        if (floating)
        {
            home.Remove(_bars);
            _bars.SetValign(Gtk.Align.End);
            _bars.SetMarginBottom(12);
            _root.AddOverlay(_bars);
        }
        else
        {
            _root.RemoveOverlay(_bars);
            _bars.SetValign(Gtk.Align.Fill);
            _bars.SetMarginBottom(0);
            home.InsertChildAfter(_bars, after);
        }
    }

    private void ApplyBarsVisibility() => _bars.SetVisible(_noticeBar?.IsOpen == true || _undoBar?.IsOpen == true);

    private static WinoInfoBarSeverity MapSeverity(InfoBarMessageType severity) => severity switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational,
    };

#if DEBUG
    internal string DebugDescribeBars()
        => $"bars={_bars.GetVisible()} notice={_noticeBar?.Describe()} undo={_undoBar?.Describe()}";

    internal void DebugCloseBar(bool undo)
    {
        if ((undo ? _undoBar : _noticeBar) is not { } bar) return;
        bar.IsOpen = false;
        if (undo) ViewModel.CloseUndoBarCommand.Execute(null);
        else ViewModel.CloseNoticeCommand.Execute(null);
    }

    internal void DebugInvokeUndo() => Observe(ViewModel.UndoTaskActionCommand.ExecuteAsync(null));
#endif
}
