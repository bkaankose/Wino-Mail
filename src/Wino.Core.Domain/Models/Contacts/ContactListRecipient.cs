using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Contacts;

/// <summary>
/// A local contact list offered as a recipient suggestion. Selecting it in the composer
/// expands to the primary address of every member.
/// </summary>
public class ContactListRecipient : IContactDisplayItem
{
    public ContactList List { get; }

    /// <summary>Members that actually have an address to send to.</summary>
    public IReadOnlyList<AccountContact> Members { get; }

    public string DisplayName => List.Name;

    /// <summary>Stands in for an address in the shared suggestion template.</summary>
    public string Address { get; }

    AccountContact IContactDisplayItem.PreviewContact => null;

    public ContactListRecipient(ContactList list, IReadOnlyList<AccountContact> members, string memberCountDescription)
    {
        List = list;
        Members = members;
        Address = memberCountDescription;
    }

    public IEnumerable<AccountContact> ExpandRecipients()
        => Members.Where(member => !string.IsNullOrWhiteSpace(member.PrimaryEmailAddress));
}
