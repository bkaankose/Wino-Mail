using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.CardDav;

public sealed class CardDavConnectionSettings
{
    public Uri ServiceUri { get; init; }
    public string AccountAddress { get; init; }
    public DavAuthenticationProfile Authentication { get; init; }
}

public sealed class CardDavDiscoveryResult
{
    public Uri ContextUri { get; init; }
    public Uri PrincipalUri { get; init; }
    public Uri AddressBookHomeUri { get; init; }
    public bool SupportsAddressBookCreation { get; init; }
    public IReadOnlyList<CardDavAddressBook> AddressBooks { get; init; } = [];
}

public sealed class CardDavAddressBook
{
    public string ExactHref { get; init; }
    public string DisplayName { get; init; }
    public string SyncToken { get; init; }
    public string CollectionTag { get; init; }
    public bool IsReadOnly { get; init; }
    public bool SupportsSyncCollection { get; init; }
    public bool SupportsMultiget { get; init; }
    public bool SupportsAddressBookQuery { get; init; }
    public bool SupportsVCard3 { get; init; } = true;
    public bool SupportsVCard4 { get; init; }
    public bool SupportsExtendedMkCol { get; init; }
    public bool SupportsAddMember { get; init; }
    public long? MaximumResourceSize { get; init; }
}

public sealed class CardDavSyncPage
{
    public IReadOnlyList<CardDavResourceChange> Changes { get; init; } = [];
    public string NextSyncToken { get; init; }
    public bool IsTruncated { get; init; }
}

public sealed class CardDavResourceChange
{
    public string ExactHref { get; init; }
    public string ETag { get; init; }
    public string VCard { get; init; }
    public bool IsDeleted { get; init; }
    public int StatusCode { get; init; }
}

public sealed class CardDavWriteResult
{
    public string ExactHref { get; init; }
    public string ETag { get; init; }
    public bool RequiresRefetch { get; init; }
}
