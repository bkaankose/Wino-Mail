using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Phone.ToDo;

/// <summary>
/// The iPhone To Do tab (iOS design decisions): Lists (smart views and task lists, Windows order) → tasks (this page:
/// sections, round checkboxes, swipes, quick add on the keyboard) → task detail (pushed). The phone shell places this
/// page over its shell-owned Lists root (<c>ModeMenuRootViewController</c> with <see cref="ToDoMenuRowStyle"/>), the way
/// Mail sits over Mailboxes; the page stays alive while popped back to Lists so a list tap shows it again at once.
/// </summary>
public sealed class PhoneToDoPageViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, UIKitNavigationService navigation)
    : ToDoPageViewController(viewModel, dispatcher, logger, navigation)
{
    private WeakReference<ToDoTaskDetailViewController>? _detail;
    private bool _keepAliveOnPop;
    private bool _releasedForGood;

    protected override bool UsesNavigationTitle => true;

    protected override bool UsesSearchController => true;

    protected override void PrepareLayout() => PageViewModel.SetCompactLayout(true);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Always;
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (IsMovingToParentViewController) return;
        // Back from the detail: the task is no longer "open".
        if (_detail is not null && _detail.TryGetTarget(out var detail)) detail.Release();
        _detail = null;
        if (PageViewModel.SelectedTasks.Count > 0) PageViewModel.BackToTaskListCommand.Execute(null);
        DeselectVisibleRows();
    }

    protected override void TaskActivated(TaskItemViewModel task)
    {
        if (NavigationController is not { } stack) return;
        if (_detail is not null && _detail.TryGetTarget(out var open) && Array.IndexOf(stack.ViewControllers ?? [], open) >= 0) return;
        var detail = CreateDetail(showsCloseButton: false);
        detail.SelectionCleared += (_, _) =>
        {
            if (ReferenceEquals(stack.TopViewController, detail)) stack.PopViewController(true);
        };
        _detail = new WeakReference<ToDoTaskDetailViewController>(detail);
        stack.PushViewController(detail, true);
    }

    /// <summary>Popping back to Lists keeps the page alive; the router releases it when the mode changes.</summary>
    public override void ViewDidDisappear(bool animated)
    {
        _keepAliveOnPop = IsMovingFromParentViewController && !_releasedForGood;
        try { base.ViewDidDisappear(animated); }
        finally { _keepAliveOnPop = false; }
    }

    public override async Task ReleaseAsync()
    {
        if (_keepAliveOnPop) return;
        _releasedForGood = true;
        await base.ReleaseAsync();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_detail is not null && _detail.TryGetTarget(out var detail)) detail.Release();
            _detail = null;
        });
    }
}
