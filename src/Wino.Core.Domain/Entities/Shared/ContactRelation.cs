using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class ContactRelation
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid ContactId { get; set; }
    public ContactRelationKind Kind { get; set; }
    public string Name { get; set; }
    public int Order { get; set; }
}
