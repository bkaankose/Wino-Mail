using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Comprehensive contact entity supporting Gmail People API, Outlook Graph Contacts API, 
/// and IMAP/vCard (RFC 2426) specifications.
/// 
/// API Mappings:
/// - Gmail: Uses Google People API v1 (people.connections)
/// - Outlook: Uses Microsoft Graph API v1.0 (contacts)
/// - IMAP: Uses vCard 3.0/4.0 (RFC 2426/6350) format
/// 
/// This entity serves as the unified contact storage that can be populated from any source
/// and synchronized bidirectionally when supported by the provider.
/// </summary>
public class Contact
{
    /// <summary>
    /// Internal unique identifier for the contact in Wino Mail database.
    /// </summary>
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    // =====================================================================
    // BASIC INFORMATION (Common across all providers)
    // =====================================================================

    /// <summary>
    /// Display name of the contact (formatted full name).
    /// Gmail: names[0].displayName
    /// Outlook: displayName
    /// vCard: FN (Formatted Name)
    /// </summary>
    [Indexed]
    public string DisplayName { get; set; }

    /// <summary>
    /// Given name / First name.
    /// Gmail: names[0].givenName
    /// Outlook: givenName
    /// vCard: N (Name) - Given Name component
    /// </summary>
    public string GivenName { get; set; }

    /// <summary>
    /// Family name / Last name / Surname.
    /// Gmail: names[0].familyName
    /// Outlook: surname
    /// vCard: N (Name) - Family Name component
    /// </summary>
    public string FamilyName { get; set; }

    /// <summary>
    /// Middle name.
    /// Gmail: names[0].middleName
    /// Outlook: middleName
    /// vCard: N (Name) - Additional Names component
    /// </summary>
    public string MiddleName { get; set; }

    /// <summary>
    /// Name prefix / Title (e.g., "Mr.", "Mrs.", "Dr.").
    /// Gmail: names[0].honorificPrefix
    /// Outlook: title
    /// vCard: N (Name) - Honorific Prefix component
    /// </summary>
    public string NamePrefix { get; set; }

    /// <summary>
    /// Name suffix (e.g., "Jr.", "Sr.", "III").
    /// Gmail: names[0].honorificSuffix
    /// Outlook: suffix
    /// vCard: N (Name) - Honorific Suffix component
    /// </summary>
    public string NameSuffix { get; set; }

    /// <summary>
    /// Nickname or preferred name.
    /// Gmail: nicknames[0].value
    /// Outlook: nickName
    /// vCard: NICKNAME
    /// </summary>
    public string Nickname { get; set; }

    /// <summary>
    /// Company/Organization name.
    /// Gmail: organizations[0].name
    /// Outlook: companyName
    /// vCard: ORG (Organization) - Name component
    /// </summary>
    public string CompanyName { get; set; }

    /// <summary>
    /// Job title or position.
    /// Gmail: organizations[0].title
    /// Outlook: jobTitle
    /// vCard: TITLE
    /// </summary>
    public string JobTitle { get; set; }

    /// <summary>
    /// Department within organization.
    /// Gmail: organizations[0].department
    /// Outlook: department
    /// vCard: ORG (Organization) - Unit component
    /// </summary>
    public string Department { get; set; }

    /// <summary>
    /// Birthday of the contact.
    /// Gmail: birthdays[0].date
    /// Outlook: birthday
    /// vCard: BDAY
    /// </summary>
    public DateTime? Birthday { get; set; }

    /// <summary>
    /// Personal website or homepage URL.
    /// Gmail: urls[0].value (where type = "home" or "homepage")
    /// Outlook: personalNotes or businessHomePage
    /// vCard: URL
    /// </summary>
    public string WebsiteUrl { get; set; }

    /// <summary>
    /// Notes or additional information about the contact.
    /// Gmail: biographies[0].value
    /// Outlook: personalNotes
    /// vCard: NOTE
    /// </summary>
    public string Notes { get; set; }

    // =====================================================================
    // CONTACT PHOTO
    // =====================================================================

    /// <summary>
    /// Base64 encoded profile/contact picture.
    /// Gmail: photos[0].url (need to download and encode)
    /// Outlook: photo (can be accessed via /photo endpoint)
    /// vCard: PHOTO (base64 encoded)
    /// </summary>
    public string Base64ContactPicture { get; set; }

    /// <summary>
    /// Remote URL for the contact photo if available.
    /// Gmail: photos[0].url
    /// Outlook: Can construct from contact ID
    /// </summary>
    public string PhotoUrl { get; set; }

    /// <summary>
    /// ETag for the photo to track changes.
    /// Gmail: photos[0].metadata.source.etag
    /// Outlook: photo@odata.mediaEtag
    /// </summary>
    public string PhotoETag { get; set; }

    // =====================================================================
    // SYNCHRONIZATION & METADATA
    // =====================================================================

    /// <summary>
    /// Source/origin of the contact.
    /// </summary>
    [Indexed]
    public ContactSource Source { get; set; } = ContactSource.Manual;

    /// <summary>
    /// Current synchronization status of the contact.
    /// </summary>
    public ContactSyncStatus SyncStatus { get; set; } = ContactSyncStatus.Synced;

    /// <summary>
    /// Remote resource ID from the source system.
    /// Gmail: resourceName (e.g., "people/c1234567890")
    /// Outlook: id
    /// IMAP: UID or unique identifier from vCard
    /// </summary>
    [Indexed]
    public string RemoteId { get; set; }

    /// <summary>
    /// ETag for optimistic concurrency control and change detection.
    /// Gmail: etag
    /// Outlook: @odata.etag or changeKey
    /// </summary>
    public string ETag { get; set; }

    /// <summary>
    /// Account ID that this contact belongs to or was synchronized from.
    /// Links to MailAccount.Id for provider-specific contacts.
    /// Null for manually created contacts that don't belong to any account.
    /// </summary>
    [Indexed]
    public Guid? AccountId { get; set; }

    /// <summary>
    /// When the contact was created locally.
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the contact was last modified locally.
    /// </summary>
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the contact was last synchronized with remote source.
    /// </summary>
    public DateTime? LastSyncDate { get; set; }

    /// <summary>
    /// Remote creation date from source API if available.
    /// Gmail: metadata.sources[0].updateTime (at creation)
    /// Outlook: createdDateTime
    /// </summary>
    public DateTime? RemoteCreatedDate { get; set; }

    /// <summary>
    /// Remote modification date from source API.
    /// Gmail: metadata.sources[0].updateTime
    /// Outlook: lastModifiedDateTime
    /// </summary>
    public DateTime? RemoteModifiedDate { get; set; }

    // =====================================================================
    // WINO-SPECIFIC FLAGS
    // =====================================================================

    /// <summary>
    /// Indicates this contact represents a root mail account in Wino.
    /// Root contacts cannot be deleted and are automatically created for each mail account.
    /// </summary>
    public bool IsRootContact { get; set; } = false;

    /// <summary>
    /// Indicates the contact has been manually modified by the user in Wino.
    /// When true, remote synchronization should be careful about overwriting local changes.
    /// </summary>
    public bool HasLocalModifications { get; set; } = false;

    /// <summary>
    /// Indicates the contact is marked as favorite/starred.
    /// Gmail: Not directly supported, can use contactGroups
    /// Outlook: flag property
    /// </summary>
    public bool IsFavorite { get; set; } = false;

    /// <summary>
    /// Indicates the contact has been soft-deleted locally.
    /// Used for synchronization before permanent deletion.
    /// </summary>
    public bool IsDeleted { get; set; } = false;

    // =====================================================================
    // CATEGORIES & GROUPS
    // =====================================================================

    /// <summary>
    /// Comma-separated list of category names or contact group IDs.
    /// Gmail: memberships (contactGroup resourceNames)
    /// Outlook: categories array
    /// vCard: CATEGORIES
    /// </summary>
    public string Categories { get; set; }

    // =====================================================================
    // INSTANT MESSAGING
    // =====================================================================

    /// <summary>
    /// Instant messaging addresses (stored as JSON array or semicolon-separated).
    /// Gmail: imClients (protocol + username)
    /// Outlook: imAddresses
    /// vCard: IMPP
    /// Format: "protocol:username" (e.g., "skype:john.doe", "gtalk:john@gmail.com")
    /// </summary>
    public string InstantMessagingAddresses { get; set; }

    // =====================================================================
    // EXTENDED PROPERTIES
    // =====================================================================

    /// <summary>
    /// Gender of the contact.
    /// Gmail: genders[0].value ("male", "female", "other", "unknown")
    /// Outlook: Not directly supported
    /// vCard: GENDER
    /// </summary>
    public string Gender { get; set; }

    /// <summary>
    /// Occupation or profession.
    /// Gmail: occupations[0].value
    /// Outlook: profession
    /// vCard: ROLE
    /// </summary>
    public string Occupation { get; set; }

    /// <summary>
    /// Manager's name.
    /// Outlook: manager
    /// </summary>
    public string Manager { get; set; }

    /// <summary>
    /// Assistant's name.
    /// Outlook: assistantName
    /// </summary>
    public string AssistantName { get; set; }

    /// <summary>
    /// Spouse's name.
    /// Outlook: spouseName
    /// </summary>
    public string SpouseName { get; set; }

    /// <summary>
    /// Anniversary date.
    /// Outlook: weddingAnniversary
    /// </summary>
    public DateTime? Anniversary { get; set; }

    // =====================================================================
    // ADDITIONAL FIELDS FOR FUTURE USE
    // =====================================================================

    /// <summary>
    /// Custom fields or extended properties stored as JSON.
    /// Can be used to store provider-specific data that doesn't fit standard fields.
    /// </summary>
    public string ExtendedProperties { get; set; }

    /// <summary>
    /// Version number for the contact record.
    /// Incremented on each modification for conflict resolution.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Raw vCard data if imported from IMAP or file.
    /// Useful for preserving all vCard fields even if not mapped to properties.
    /// </summary>
    public string RawVCard { get; set; }

    // =====================================================================
    // COMPUTED PROPERTIES (Not stored in database)
    // =====================================================================

    /// <summary>
    /// Gets whether this contact has been synchronized from a remote source.
    /// </summary>
    [Ignore]
    public bool IsRemoteSynced => !string.IsNullOrEmpty(RemoteId);

    /// <summary>
    /// Gets whether this contact supports bidirectional synchronization.
    /// Gmail and Outlook support full sync, IMAP/EmailExtraction are read-only.
    /// </summary>
    [Ignore]
    public bool SupportsBidirectionalSync => Source == ContactSource.Gmail || Source == ContactSource.Outlook;

    /// <summary>
    /// Gets a search-friendly full name.
    /// </summary>
    [Ignore]
    public string SearchableName => $"{GivenName} {MiddleName} {FamilyName} {Nickname} {DisplayName}".Trim();
}
