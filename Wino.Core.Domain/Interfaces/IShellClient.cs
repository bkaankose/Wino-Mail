#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Interfaces;

public interface IShellClient : INotifyPropertyChanged
{
    WinoApplicationMode Mode { get; }
    IDispatcher Dispatcher { get; set; }
    MenuItemCollection? MenuItems { get; }
    object? SelectedMenuItem { get; set; }
    bool HandlesNavigationSelection { get; }

    void Activate(ShellModeActivationContext activationContext);
    void Deactivate();
    Task HandleNavigationItemInvokedAsync(IMenuItem? menuItem);
    Task HandleNavigationSelectionChangedAsync(IMenuItem? menuItem);
    Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args);
}

public interface IMailShellClient : IShellClient
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

public interface ICalendarShellClient : IShellClient
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

public interface IShellViewModel
{
    WinoApplicationMode CurrentMode { get; }
    IShellClient CurrentClient { get; }
    MenuItemCollection? CurrentMenuItems { get; }
    object? SelectedMenuItem { get; set; }

    void SetCurrentMode(WinoApplicationMode mode);
    IShellClient GetClient(WinoApplicationMode mode);
}

public interface IShellHost
{
    bool HasShellContent { get; }

    void ActivateMode(WinoApplicationMode mode, ShellModeActivationContext activationContext);
}
