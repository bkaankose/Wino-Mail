using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

/// <summary>
/// Emitted when synchronizer state is updated.
/// </summary>
/// <param name="AccountId">Account id</param>
/// <param name="NewState">New synchronizer state</param>
/// <param name="TotalItemsToSync">Total items to sync (0 for indeterminate)</param>
/// <param name="RemainingItemsToSync">Remaining items to sync</param>
/// <param name="SynchronizationStatus">Current synchronization status message</param>
public record AccountSynchronizerStateChanged(
    Guid AccountId, 
    AccountSynchronizerState NewState,
    int TotalItemsToSync = 0,
    int RemainingItemsToSync = 0,
    string SynchronizationStatus = "") : UIMessageBase<AccountSynchronizerStateChanged>;
