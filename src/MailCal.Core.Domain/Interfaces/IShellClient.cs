#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Mail specific surface used by the mail menu item templates and the mail drag and drop
/// behaviours. The shell itself never touches this.
/// </summary>
public interface IMailShellClient : IShellMenuProvider
{
    IMenuItem CreatePrimaryMenuItem { get; }

    IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folder);
    Task HandleAccountCreatedAsync(MailAccount createdAccount);
    Task NavigateFolderAsync(IBaseFolderMenuItem baseFolderMenuItem, TaskCompletionSource<bool>? folderInitAwaitTask = null);
    Task ChangeLoadedAccountAsync(IAccountMenuItem clickedBaseAccountMenuItem, bool navigateInbox = true);
    Task PerformFolderOperationAsync(FolderOperation operation, IBaseFolderMenuItem folderMenuItem);

    /// <summary>
    /// Marks every mail in the Inbox of each account held by the menu item as read.
    /// A merged account covers the Inbox of every linked account.
    /// </summary>
    Task MarkAccountInboxesAsReadAsync(IAccountMenuItem accountMenuItem);
    Task PerformMoveOperationAsync(IEnumerable<MailCopy> items, IBaseFolderMenuItem targetFolderMenuItem);
    Task CreateRootFolderAsync(IAccountMenuItem accountMenuItem);
    Task CreateNewMailForAsync(MailAccount account);

    /// <summary>
    /// Runs the fix for an account that needs attention, such as signing in again.
    /// </summary>
    Task HandleAccountAttentionAsync(MailAccount account);
}

/// <summary>
/// Calendar specific surface bound by the calendar pane menu item templates.
/// </summary>
public interface ICalendarShellClient : IShellMenuProvider
{
    IStatePersistanceService StatePersistenceService { get; }
    IEnumerable DateNavigationHeaderItems { get; }
    int SelectedDateNavigationHeaderIndex { get; }
    VisibleDateRange? CurrentVisibleRange { get; }
    string VisibleDateRangeText { get; }
    bool CanSynchronizeCalendars { get; }
    ICommand SyncCommand { get; }
    ICommand TodayClickedCommand { get; }
    ICommand DateClickedCommand { get; }
    ICommand PreviousDateRangeCommand { get; }
    ICommand NextDateRangeCommand { get; }
    IEnumerable GroupedAccountCalendars { get; }
}

/// <summary>
/// The shell's own view model. It hosts whatever menu the navigated page published and
/// knows nothing else about the content.
/// </summary>
public interface IShellViewModel
{
    MailCalApplicationMode CurrentMode { get; }
    ShellMenu? CurrentMenu { get; }
    object? SelectedMenuItem { get; set; }

    void SetCurrentMode(MailCalApplicationMode mode);
    IShellMenuProvider GetProvider(MailCalApplicationMode mode);
}

/// <summary>
/// The page hosting the inner shell frame.
/// </summary>
public interface IShellHost
{
    bool HasShellContent { get; }

    void ActivateMode(MailCalApplicationMode mode, ShellModeActivationContext activationContext);
}
