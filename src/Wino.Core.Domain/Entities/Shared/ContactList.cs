using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A local, user-created list of contacts. Lists are never synchronized to a provider and
/// may hold contacts that belong to different accounts.
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
}
