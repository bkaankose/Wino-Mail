using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Models.CardDav;

public sealed record CardDavRemoteUpsert(AccountContact Contact, CardDavResourceShadow Shadow);

public sealed class CardDavRemotePage
{
    public Guid AddressBookId { get; init; }
    public IReadOnlyList<CardDavRemoteUpsert> Upserts { get; init; } = [];
    public IReadOnlyList<string> SeenHrefs { get; init; } = [];
    public IReadOnlyList<string> DeletedHrefs { get; init; } = [];
    public IReadOnlyList<CardDavQuarantine> Quarantines { get; init; } = [];
    public string NextSyncToken { get; init; }
    public long ReconciliationGeneration { get; init; }
    public bool CommitSyncToken { get; init; }
    public bool IsFullReconciliation { get; init; }
}

public sealed record CardDavBookBinding(ContactAddressBook AddressBook, CardDavAddressBookState State);

public sealed record CardDavStagedMutation(ContactOperationPreparationRequest Request, CardDavOutboxItem OutboxItem);
