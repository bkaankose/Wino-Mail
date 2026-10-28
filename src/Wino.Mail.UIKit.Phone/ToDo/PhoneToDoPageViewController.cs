using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.UIKit.Views.ToDo;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Phone.ToDo;

/// <summary>
/// The iPhone To Do tab (iOS design decisions): tasks (this page: sections, round checkboxes, swipes, quick add on the
/// keyboard) → task detail (pushed). The page is the tab's root; Lists (smart views and task lists, Windows order) are in
/// the shell drawer (<c>ModeMenuRootViewController</c> with <see cref="ToDoMenuRowStyle"/>), the way Mailboxes is.
/// </summary>
public sealed class PhoneToDoPageViewController(ToDoPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, UIKitNavigationService navigation)
    : ToDoPageViewController(viewModel, dispatcher, logger, navigation), IPhoneFloatingActionPage
{
    private WeakReference<ToDoTaskDetailViewController>? _detail;
    private bool _keepAliveOnPop;
    private bool _releasedForGood;

    protected override bool UsesNavigationTitle => true;

    protected override bool UsesSearchController => true;

    protected override bool RowSeparators => true;

    protected override bool CombinesBarMenus => true;

    protected override bool QuickAddOnDemand => true;

    /// <summary>The shell's floating button: the quick add bar on the keyboard.</summary>
    void IPhoneFloatingActionPage.FloatingActionInvoked() => ShowQuickAdd();

    protected override void PrepareLayout() => PageViewModel.SetCompactLayout(true);

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        // Search sits behind the bar's search glyph on every tab.
        if (NavigationItem.SearchController is { } search) PhoneBarItems.BarSearch(NavigationItem, search);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        if (IsMovingToParentViewController || NavigationController is ModeStackController { IsPaired: true }) return;
        // Back from the detail: the task is no longer "open".
        if (_detail is not null && _detail.TryGetTarget(out var detail)) detail.Release();
        _detail = null;
        if (PageViewModel.SelectedTasks.Count > 0) PageViewModel.BackToTaskListCommand.Execute(null);
        DeselectVisibleRows();
    }

    protected override void TaskActivated(TaskItemViewModel task)
    {
        if (NavigationController is not { } stack) return;
        if (_detail is not null && _detail.TryGetTarget(out var open) && Array.IndexOf(stack.LogicalViewControllers(), open) >= 0) return;
        var detail = CreateDetail(showsCloseButton: false);
        detail.SelectionCleared += (_, _) =>
        {
            if (ReferenceEquals(stack.LogicalTop(), detail)) stack.PopViewController(true);
        };
        _detail = new WeakReference<ToDoTaskDetailViewController>(detail);
        PhoneShellViewController.PrepareForTransition(detail, stack);
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
