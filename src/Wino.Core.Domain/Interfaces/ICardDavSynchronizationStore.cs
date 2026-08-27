using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

public interface ICardDavSynchronizationStore
{
    Task<CardDavAccountState> GetAccountStateAsync(Guid accountId);
    Task SaveDiscoveryAsync(Guid accountId, CardDavDiscoveryResult discovery);
    Task<IReadOnlyList<CardDavBookBinding>> GetAddressBooksAsync(Guid accountId, Guid? addressBookId = null);
    Task<CardDavResourceShadow> GetShadowByHrefAsync(Guid addressBookId, string exactHref);
    Task<CardDavResourceShadow> GetShadowByContactAsync(Guid contactId);
    Task<long> BeginFullReconciliationAsync(Guid addressBookId);
    Task ApplyRemotePageAsync(CardDavRemotePage page);
    Task CompleteFullReconciliationAsync(Guid addressBookId, long generation, string syncToken = null);
    Task<CardDavOutboxItem> StageMutationAsync(ContactOperationPreparationRequest request);
    Task<IReadOnlyList<CardDavOutboxItem>> LeaseDueOutboxAsync(Guid accountId, Guid? addressBookId, int maximumCount, TimeSpan leaseDuration);
    Task UpdateOutboxTargetAsync(Guid outboxItemId, string intendedHref);
    Task CompleteOutboxAsync(Guid outboxItemId, AccountContact serverContact, CardDavResourceShadow shadow, bool deleted);
    Task RescheduleOutboxAsync(Guid outboxItemId, string errorCode, DateTime nextAttemptUtc);
    Task BlockOutboxWithConflictAsync(CardDavOutboxItem outboxItem, CardDavConflict conflict);
    Task<IReadOnlyList<CardDavConflict>> GetUnresolvedConflictsAsync(Guid? accountId = null);
    Task<CardDavConflictDetails> GetConflictDetailsAsync(Guid conflictId);
    Task ResolveConflictAsync(Guid conflictId, CardDavConflictResolution resolution);
    Task DeleteAddressBookStateAsync(Guid addressBookId);
    Task DeleteAccountStateAsync(Guid accountId);
}
