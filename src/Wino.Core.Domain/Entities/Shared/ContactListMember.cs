using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Membership of a single contact in a <see cref="ContactList"/>.
/// </summary>
public class ContactListMember
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid ListId { get; set; }
    [Indexed] public Guid ContactId { get; set; }
}
