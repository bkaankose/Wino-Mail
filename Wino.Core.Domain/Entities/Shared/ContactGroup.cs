using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A named group of contacts that can be expanded to individual addresses during mail composition.
/// </summary>
public class ContactGroup
{
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>Display name of the group (e.g., "Team Alpha", "Family").</summary>
    public string Name { get; set; }

    /// <summary>Optional description for the group.</summary>
    public string Description { get; set; }
}
