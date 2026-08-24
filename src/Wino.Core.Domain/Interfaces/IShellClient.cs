#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

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
    Task PerformMoveOperationAsync(IEnumerable<MailCopy> items, IBaseFolderMenuItem targetFolderMenuItem);
    Task CreateRootFolderAsync(IAccountMenuItem accountMenuItem);
    Task CreateNewMailForAsync(MailAccount account);
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
    WinoApplicationMode CurrentMode { get; }
    ShellMenu? CurrentMenu { get; }
    object? SelectedMenuItem { get; set; }

    void SetCurrentMode(WinoApplicationMode mode);
    IShellMenuProvider GetProvider(WinoApplicationMode mode);
}

/// <summary>
/// The page hosting the inner shell frame.
/// </summary>
public interface IShellHost
{
    bool HasShellContent { get; }

    void ActivateMode(WinoApplicationMode mode, ShellModeActivationContext activationContext);
}
