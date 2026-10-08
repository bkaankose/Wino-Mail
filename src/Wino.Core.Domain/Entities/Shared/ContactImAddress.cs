using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public class ContactImAddress
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid ContactId { get; set; }
    public string Address { get; set; }
    public string Protocol { get; set; }
    public int Order { get; set; }
}
