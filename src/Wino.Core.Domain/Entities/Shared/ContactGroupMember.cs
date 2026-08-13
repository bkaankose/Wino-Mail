using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Associates an e-mail address with a <see cref="ContactGroup"/>.
/// </summary>
public class ContactGroupMember
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Group this member belongs to.</summary>
    [Indexed]
    public Guid GroupId { get; set; }

    /// <summary>E-mail address of the member (FK to AccountContact.Address).</summary>
    [Indexed]
    public string MemberAddress { get; set; }
}
