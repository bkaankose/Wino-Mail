using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Mail's half of the shell title bar synchronization button. Everything reported here is
/// scoped to the account the pane currently has loaded, and the progress is read out of
/// <see cref="SynchronizationManager"/> on demand instead of being mirrored into a field.
/// </summary>
public partial class MailAppShellViewModel
{
    public bool IsSynchronizationSupported => true;

    public bool CanSynchronize => SelectedAccountIds.Count > 0 && !SynchronizationState.IsSynchronizing;

    public ShellSynchronizationState SynchronizationState
        => ShellSynchronizationStateReader.Read(SelectedAccountIds, SynchronizationProgressCategory.Mail);

    public string SynchronizationDescription
    {
        get
        {
            var accountName = latestSelectedAccountMenuItem switch
            {
                null => null,
                var menuItem => menuItem.HoldingAccounts?.Count() == 1
                    ? menuItem.HoldingAccounts.First().Name
                    : null
            };

            return string.IsNullOrWhiteSpace(accountName)
                ? Translator.TitleBar_Sync_MailDescriptionGeneric
                : string.Format(Translator.TitleBar_Sync_MailDescription, accountName);
        }
    }

    public string SynchronizationToolTip => Translator.TitleBar_Sync_MailToolTip;

    public Task SynchronizeAsync()
    {
        foreach (var accountId in SelectedAccountIds)
        {
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        RefreshShellSynchronizationState();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The accounts behind the selected pane entry. A merged inbox contributes all of them,
    /// so the button reports the whole group rather than an arbitrary member of it.
    /// </summary>
    private IReadOnlyList<Guid> SelectedAccountIds
        => latestSelectedAccountMenuItem?.HoldingAccounts?.Select(account => account.Id).ToList()
           ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();

    /// <summary>
    /// Nothing here is stored, so a refresh is only a notification. Called whenever the
    /// loaded account changes or the manager reports new mail progress.
    /// </summary>
    internal void RefreshShellSynchronizationState()
    {
        OnPropertyChanged(nameof(CanSynchronize));
        OnPropertyChanged(nameof(SynchronizationState));
        OnPropertyChanged(nameof(SynchronizationDescription));
        OnPropertyChanged(nameof(SynchronizationToolTip));
    }
}
