using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A list of contacts. A list belongs to one address book of one account and only holds
/// contacts of that address book. It is stored where the address book is: on the device
/// for a local address book, as a group on the server for a CardDAV one, as a contact
/// group for a Google one.
/// </summary>
public class ContactList
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
    public string Description { get; set; }
    public string ColorHex { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    [Indexed] public Guid? MailAccountId { get; set; }
    [Indexed] public Guid? AddressBookId { get; set; }

    /// <summary>
    /// The provider's id of the list, for a provider that names its lists itself: the
    /// resource name of a Google contact group. Empty for a local or CardDAV list.
    /// </summary>
    [Indexed] public string RemoteId { get; set; }
}
