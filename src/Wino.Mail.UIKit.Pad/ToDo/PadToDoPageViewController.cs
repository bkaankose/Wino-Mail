using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Shell;
using Wino.Mail.UIKit.Views.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Pad.ToDo;

/// <summary>
/// The iPad To Do mode (Mac Revision 2, Windows layout): the sidebar shows the smart views and task lists (the shell's
/// generic rows), the list column this page and the reader column the task detail drawer, which follows the selection and
/// shows the bulk actions for a multiple selection. The toolbar search filters the surface.
/// </summary>
public sealed class PadToDoPageViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, UIKitNavigationService navigation)
    : ToDoPageViewController(viewModel, dispatcher, logger, navigation)
{
    private ToDoTaskDetailViewController? _detail;

    protected override bool UsesNavigationTitle => false;

    protected override bool UsesSearchController => false;

    protected override void PrepareLayout()
    {
        PageViewModel.SetCompactLayout(false);
        EnsureDetail();
    }

    protected override void TaskActivated(TaskItemViewModel task) => EnsureDetail();

    private void EnsureDetail()
    {
        if (IsReleased || Navigation.Shell is not PadShellViewController shell || shell.ActiveMode != WinoApplicationMode.Tasks) return;
        if (_detail is not null && ReferenceEquals(shell.ReaderFrame.Page, _detail)) return;
        _detail?.Release();
        _detail = CreateDetail(showsCloseButton: true);
        shell.ShowInReader(_detail);
    }

    public override async Task ReleaseAsync()
    {
        var detail = _detail;
        _detail = null;
        await base.ReleaseAsync();
        if (detail is null) return;
        Task clear = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            detail.Release();
            if (Navigation.Shell is PadShellViewController shell && ReferenceEquals(shell.ReaderFrame.Page, detail)) clear = shell.ReaderFrame.ClearAsync();
        });
        await clear;
    }
}
