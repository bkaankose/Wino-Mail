using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.CardDav;

public sealed record CardDavBookBinding(ContactAddressBook AddressBook, CardDavAddressBookState State);

/// <summary>A downloaded person vCard. <paramref name="ContactId"/> is the local row it was written to.</summary>
public sealed record CardDavRemoteContact(string Href, string ETag, string Uid, Guid ContactId);

/// <summary>A downloaded group vCard.</summary>
public sealed record CardDavRemoteGroup(string Href, string ETag, string Uid, string Name, IReadOnlyList<string> MemberUids);

/// <summary>
/// Resource bookkeeping for one applied batch of remote changes. Contact rows themselves
/// are written through the contact service; this records their identity, the groups and
/// the synchronization checkpoint.
/// </summary>
public sealed class CardDavChangeSet
{
    public Guid AccountId { get; init; }
    public Guid AddressBookId { get; init; }
    public IReadOnlyList<CardDavRemoteContact> Contacts { get; init; } = [];
    public IReadOnlyList<CardDavRemoteGroup> Groups { get; init; } = [];
    public IReadOnlyList<string> DeletedHrefs { get; init; } = [];

    /// <summary>True for the batch that completes a synchronization and stores its checkpoint.</summary>
    public bool CommitCheckpoint { get; init; }
    public string SyncToken { get; init; }
    public string CollectionTag { get; init; }
}
