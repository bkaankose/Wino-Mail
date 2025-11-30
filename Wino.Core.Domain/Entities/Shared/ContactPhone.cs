using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Represents a phone number associated with a contact.
/// Based on Gmail People API phoneNumbers and Outlook Graph phones.
/// </summary>
public class ContactPhone
{
    /// <summary>
    /// Primary key for the phone number.
    /// </summary>
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the parent Contact.
    /// </summary>
    [Indexed]
    public Guid ContactId { get; set; }

    /// <summary>
    /// The phone number value in E.164 format when possible.
    /// Example: +1 (555) 123-4567
    /// </summary>
    public string Number { get; set; }

    /// <summary>
    /// Type/label of the phone number (e.g., "mobile", "home", "work", "fax").
    /// Maps to Gmail People API "type" field and Outlook "type" field.
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Indicates if this is the primary/preferred phone number for this contact.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Order/rank of this phone number in the list.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Canonical form of the phone number in E.164 format.
    /// Used for better matching and deduplication.
    /// Example: +15551234567
    /// </summary>
    public string CanonicalForm { get; set; }
}
