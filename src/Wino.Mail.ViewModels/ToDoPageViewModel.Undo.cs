using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The undo bar for completing, deleting and moving tasks. The request waits in the synchronization
/// manager's undo window before it reaches the server; Undo reverts the optimistic change and nothing is sent.
/// </summary>
public partial class ToDoPageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUndoBarOpen))]
    public partial UndoableMailActionPack CurrentUndoPack { get; set; }

    public bool IsUndoBarOpen => CurrentUndoPack is not null;

    public string UndoBarTitle => CurrentUndoPack?.Title ?? string.Empty;

    partial void OnCurrentUndoPackChanged(UndoableMailActionPack value) => OnPropertyChanged(nameof(UndoBarTitle));

    [RelayCommand]
    private async Task UndoTaskActionAsync()
    {
        var pack = CurrentUndoPack;
        if (pack is null)
            return;

        foreach (var accountId in pack.AccountIds.Distinct())
            await _synchronizationManager.UndoLatestQueuedAction(accountId).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (CurrentUndoPack?.Id == pack.Id)
                CurrentUndoPack = null;
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private void CloseUndoBar() => CurrentUndoPack = null;

    private void ReceiveUndoPack(UndoableMailActionPackChanged message)
    {
        if (message?.Pack is not { Kind: UndoableActionKind.Task } pack)
            return;

        _ = ExecuteUIThread(() =>
        {
            if (message.State == UndoableMailActionPackState.Queued)
                CurrentUndoPack = pack;
            else if (CurrentUndoPack?.Id == pack.Id)
                CurrentUndoPack = null;
        });
    }
}
