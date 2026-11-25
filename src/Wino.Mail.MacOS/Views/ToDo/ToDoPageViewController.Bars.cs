using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.ToDo;

/// <summary>
/// The notice and undo bars above quick add (Windows ToDoPage notice and undo InfoBars). The notice reports copy, import
/// and export results; the undo bar holds a complete, delete or move until it closes or Undo is pressed.
/// </summary>
public sealed partial class ToDoPageViewController
{
    private NSStackView _bars = null!;
    private WinoInfoBar _noticeBar = null!;
    private WinoInfoBar _undoBar = null!;

    private NSStackView BuildBars()
    {
        _noticeBar = new WinoInfoBar { Hidden = true, IsClosable = true };
        _noticeBar.Closed += (_, _) => ViewModel.CloseNoticeCommand.Execute(null);

        _undoBar = new WinoInfoBar(WinoInfoBarSeverity.Informational) { Hidden = true, IsClosable = true, ActionTitle = Translator.UndoActions_Button };
        _undoBar.ActionInvoked += (_, _) => Observe(ViewModel.UndoTaskActionCommand.ExecuteAsync(null));
        _undoBar.Closed += (_, _) => ViewModel.CloseUndoBarCommand.Execute(null);

        _bars = WinoLayout.VStack(6, _noticeBar, _undoBar);
        _bars.DetachesHiddenViews = true;
        return _bars;
    }

    private void BindBars()
    {
        Bind(nameof(ViewModel.IsNoticeOpen), static vm => vm.IsNoticeOpen, _ => ApplyNotice());
        Bind(nameof(ViewModel.NoticeTitle), static vm => vm.NoticeTitle, _ => ApplyNotice());
        Bind(nameof(ViewModel.NoticeMessage), static vm => vm.NoticeMessage, _ => ApplyNotice());
        Bind(nameof(ViewModel.NoticeSeverity), static vm => vm.NoticeSeverity, _ => ApplyNotice());
        Bind(nameof(ViewModel.IsUndoBarOpen), static vm => vm.IsUndoBarOpen, _ => ApplyUndo());
        Bind(nameof(ViewModel.UndoBarTitle), static vm => vm.UndoBarTitle, _ => ApplyUndo());
    }

    private void ApplyNotice()
    {
        _noticeBar.Title = ViewModel.NoticeTitle;
        _noticeBar.Message = ViewModel.NoticeMessage;
        _noticeBar.Severity = MapSeverity(ViewModel.NoticeSeverity);
        _noticeBar.Hidden = !ViewModel.IsNoticeOpen;
    }

    private void ApplyUndo()
    {
        _undoBar.Title = ViewModel.UndoBarTitle;
        _undoBar.Hidden = !ViewModel.IsUndoBarOpen;
    }

    private static WinoInfoBarSeverity MapSeverity(InfoBarMessageType severity) => severity switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };
}
