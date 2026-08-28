using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels;

/// <summary>
/// To Do's half of the shell title bar synchronization button. Scoped to the accounts the
/// page can actually synchronize tasks for, with the progress read out of
/// <see cref="SynchronizationManager"/> rather than mirrored here.
/// </summary>
public partial class ToDoPageViewModel
{
    public bool IsSynchronizationSupported => true;

    public bool CanSynchronize => TaskAccountIds.Count > 0 && !SynchronizationState.IsSynchronizing;

    public ShellSynchronizationState SynchronizationState
        => ShellSynchronizationStateReader.Read(TaskAccountIds, SynchronizationProgressCategory.Tasks);

    public string SynchronizationDescription => Translator.TitleBar_Sync_TasksDescription;

    public string SynchronizationToolTip => Translator.TitleBar_Sync_TasksToolTip;

    public Task SynchronizeAsync()
    {
        Synchronize();
        RefreshShellSynchronizationState();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors the account filter <see cref="Synchronize"/> applies, so the button never
    /// reports progress for an account it would refuse to synchronize.
    /// </summary>
    private IReadOnlyList<Guid> TaskAccountIds
        => Accounts
            .Where(account => account.ProviderType is (MailProviderType.Gmail or MailProviderType.Outlook) &&
                              account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
            .Select(account => account.Id)
            .ToList();

    internal void RefreshShellSynchronizationState()
    {
        OnPropertyChanged(nameof(CanSynchronize));
        OnPropertyChanged(nameof(SynchronizationState));
        OnPropertyChanged(nameof(SynchronizationDescription));
        OnPropertyChanged(nameof(SynchronizationToolTip));
    }
}
