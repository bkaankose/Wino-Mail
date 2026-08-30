using System.Collections.Generic;
using System.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Common presentation contract for account selectors used by shell modes.
/// </summary>
public interface IAccountNavigationMenuItem : IMenuItem, INotifyPropertyChanged
{
    MailAccount Account { get; }
    string AccountName { get; }
    string AccountAddress { get; }
    int UnreadItemCount { get; }
    bool IsSynchronizationProgressVisible { get; }
    bool IsProgressIndeterminate { get; }
    double SynchronizationProgressValue { get; }
    bool IsAttentionRequired { get; }
    bool SupportsMailAccountActions { get; }

    /// <summary>
    /// Whether invoking the row is a selection. Mail and tasks expand an account
    /// into its children, while a contacts address book is itself the destination.
    /// </summary>
    bool SelectsOnInvoked { get; }
}

public interface IAccountMenuItem : IMenuItem
{
    bool IsEnabled { get; set; }
    bool IsSynchronizationInProgress { get; set; }

    /// <summary>
    /// Calculated synchronization progress percentage (0-100).
    /// </summary>
    double SynchronizationProgress { get; }

    /// <summary>
    /// Progress value clamped for XAML progress controls.
    /// </summary>
    double SynchronizationProgressValue { get; }

    /// <summary>
    /// Total items to sync. 0 for indeterminate progress.
    /// </summary>
    int TotalItemsToSync { get; set; }

    /// <summary>
    /// Remaining items to sync.
    /// </summary>
    int RemainingItemsToSync { get; set; }

    /// <summary>
    /// Current synchronization status message.
    /// </summary>
    string SynchronizationStatus { get; set; }

    int UnreadItemCount { get; set; }
    IEnumerable<MailAccount> HoldingAccounts { get; }
    void ApplySynchronizationProgress(AccountSynchronizationProgress progress);
    void UpdateAccount(MailAccount account);
}

public interface IMergedAccountMenuItem : IAccountMenuItem
{
    int MergedAccountCount { get; }

    MergedInbox Parameter { get; }
}
