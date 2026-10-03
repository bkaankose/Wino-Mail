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
    /// <summary>Null when only the address-book home was listed.</summary>
    public Uri ContextUri { get; init; }

    /// <summary>Null when only the address-book home was listed.</summary>
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
    public bool SupportsVCard4 { get; init; }
}

public sealed class CardDavSyncPage
{
    public IReadOnlyList<CardDavResourceChange> Changes { get; init; } = [];
    public string NextSyncToken { get; init; }

    /// <summary>The server stopped early (507); ask again with <see cref="NextSyncToken"/>.</summary>
    public bool IsTruncated { get; init; }
}

public sealed class CardDavResourceChange
{
    public string ExactHref { get; init; }
    public string ETag { get; init; }
    public string VCard { get; init; }
    public bool IsDeleted { get; init; }
}

public sealed class CardDavWriteResult
{
    public string ExactHref { get; init; }

    /// <summary>Null when the server did not return a strong ETag for the stored resource.</summary>
    public string ETag { get; init; }
}
