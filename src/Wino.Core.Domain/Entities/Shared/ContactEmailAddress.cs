using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

public class ContactEmailAddress
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid ContactId { get; set; }
    public string Address { get; set; }
    [Indexed] public string NormalizedAddress { get; set; }
    public string Label { get; set; }
    public int Order { get; set; }
    public bool IsPrimary { get; set; }
    public static string Normalize(string address) => address?.Trim().ToUpperInvariant();
}
