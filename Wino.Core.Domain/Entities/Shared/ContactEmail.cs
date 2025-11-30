using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Represents an email address associated with a contact.
/// Supports multiple email addresses per contact as per Gmail People API and Outlook Graph API.
/// </summary>
public class ContactEmail
{
    /// <summary>
    /// Primary key for the email address.
    /// </summary>
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the parent Contact.
    /// </summary>
    [Indexed]
    public Guid ContactId { get; set; }

    /// <summary>
    /// The email address value.
    /// Example: john.doe@example.com
    /// </summary>
    [Indexed]
    public string Address { get; set; }

    /// <summary>
    /// Type/label of the email (e.g., "home", "work", "other").
    /// Maps to Gmail People API "type" field and Outlook "type" field.
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Display name for this specific email if different from contact name.
    /// Optional field.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Indicates if this is the primary/preferred email address for this contact.
    /// Only one email should be marked as primary per contact.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Order/rank of this email address in the list.
    /// Used for display ordering.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Remote resource identifier from source API.
    /// - Gmail: Not typically used for email fields
    /// - Outlook: Email address resource ID if available
    /// </summary>
    public string RemoteId { get; set; }
}
