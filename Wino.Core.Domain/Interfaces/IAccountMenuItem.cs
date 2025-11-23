using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

public interface IAccountMenuItem : IMenuItem
{
    bool IsEnabled { get; set; }
    
    /// <summary>
    /// Calculated synchronization progress percentage (0-100). -1 for indeterminate.
    /// </summary>
    double SynchronizationProgress { get; }
    
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
    void UpdateAccount(MailAccount account);
}

public interface IMergedAccountMenuItem : IAccountMenuItem
{
    int MergedAccountCount { get; }

    MergedInbox Parameter { get; }
}
