using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Represents a physical address associated with a contact.
/// Based on Gmail People API addresses and Outlook Graph postalAddresses.
/// </summary>
public class ContactAddress
{
    /// <summary>
    /// Primary key for the address.
    /// </summary>
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to the parent Contact.
    /// </summary>
    [Indexed]
    public Guid ContactId { get; set; }

    /// <summary>
    /// Type/label of the address (e.g., "home", "work", "other").
    /// Maps to Gmail People API "type" field and Outlook "type" field.
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Unstructured formatted address string.
    /// This is the complete address as a single string.
    /// Gmail: formattedValue, Outlook: full address
    /// </summary>
    public string FormattedAddress { get; set; }

    /// <summary>
    /// Street address including street number and name.
    /// Example: "123 Main Street, Apt 4B"
    /// Gmail: streetAddress, Outlook: street
    /// </summary>
    public string Street { get; set; }

    /// <summary>
    /// City or locality.
    /// Gmail: city, Outlook: city
    /// </summary>
    public string City { get; set; }

    /// <summary>
    /// State, province, or region.
    /// Gmail: region, Outlook: state
    /// </summary>
    public string State { get; set; }

    /// <summary>
    /// Postal or ZIP code.
    /// Gmail: postalCode, Outlook: postalCode
    /// </summary>
    public string PostalCode { get; set; }

    /// <summary>
    /// Country.
    /// Gmail: country, Outlook: countryOrRegion
    /// </summary>
    public string Country { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g., "US", "GB", "TR").
    /// Gmail: countryCode
    /// </summary>
    public string CountryCode { get; set; }

    /// <summary>
    /// Indicates if this is the primary/preferred address for this contact.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Order/rank of this address in the list.
    /// </summary>
    public int Order { get; set; }
}
