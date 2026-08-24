using System;
using System.Collections.Generic;
using System.Linq;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

[Table("ContactCard")]
public class AccountContact : IEquatable<AccountContact>, IContactDisplayItem
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();
    [Indexed] public Guid MailAccountId { get; set; }
    [Indexed] public Guid AddressBookId { get; set; }
    [Indexed] public ContactSourceKind SourceKind { get; set; }
    public string RemoteId { get; set; }
    public string RemoteVersion { get; set; }
    public string RemotePhotoKey { get; set; }
    public string DisplayName { get; set; }
    public string HonorificPrefix { get; set; }
    public string GivenName { get; set; }
    public string MiddleName { get; set; }
    public string Surname { get; set; }
    public string HonorificSuffix { get; set; }
    public string Nickname { get; set; }
    public string FileAs { get; set; }
    public string CompanyName { get; set; }
    public string Department { get; set; }
    public string JobTitle { get; set; }
    public string OfficeLocation { get; set; }
    public string Profession { get; set; }
    public int? BirthdayYear { get; set; }
    public int? BirthdayMonth { get; set; }
    public int? BirthdayDay { get; set; }
    public string Notes { get; set; }
    public string Website { get; set; }
    public Guid? ContactPictureFileId { get; set; }
    public bool IsAutoCollected { get; set; }

    /// <summary>
    /// Local-only favorite marker. Never synchronized to a provider, and preserved
    /// across full and delta contact synchronization.
    /// </summary>
    [Indexed] public bool IsFavorite { get; set; }
    public ContactPendingMutation PendingMutation { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
    public string SortKey { get; set; }

    [Ignore] public List<ContactEmailAddress> EmailAddresses { get; set; } = [];
    [Ignore] public List<ContactPhoneNumber> PhoneNumbers { get; set; } = [];
    [Ignore] public List<ContactPostalAddress> PostalAddresses { get; set; } = [];
    [Ignore] public List<ContactImAddress> ImAddresses { get; set; } = [];
    [Ignore] public List<ContactRelation> Relations { get; set; } = [];

    // Compatibility projections for mail/calendar callers while persistence moves to contact IDs.
    [Ignore]
    public string Address
    {
        get => PrimaryEmailAddress;
        set
        {
            EmailAddresses ??= [];
            var primary = EmailAddresses.FirstOrDefault(a => a.IsPrimary) ?? EmailAddresses.FirstOrDefault();
            if (primary is null)
            {
                primary = new ContactEmailAddress { ContactId = Id, IsPrimary = true };
                EmailAddresses.Add(primary);
            }

            primary.Address = value;
            primary.NormalizedAddress = ContactEmailAddress.Normalize(value);
        }
    }

    [Ignore] public string Name { get => DisplayName; set => DisplayName = value; }
    [Ignore] public bool IsRootContact { get; set; }
    [Ignore]
    public bool IsOverridden
    {
        get => !IsAutoCollected;
        set { if (value) IsAutoCollected = false; }
    }

    [Ignore]
    public string PrimaryEmailAddress => EmailAddresses?.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Order)
        .Select(a => a.Address).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    [Ignore]
    public string PrimaryPhoneNumber => PhoneNumbers?.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Order)
        .Select(a => a.Number).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    [Ignore]
    public string DisplayValue => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(CompanyName) ? CompanyName
        : PrimaryEmailAddress ?? PrimaryPhoneNumber ?? string.Empty;

    AccountContact IContactDisplayItem.PreviewContact => this;

    public override bool Equals(object obj)
    {
        return Equals(obj as AccountContact);
    }

    public bool Equals(AccountContact other)
    {
        return other is not null && Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public override string ToString() => PrimaryEmailAddress ?? DisplayValue;

    public static bool operator ==(AccountContact left, AccountContact right)
    {
        return EqualityComparer<AccountContact>.Default.Equals(left, right);
    }

    public static bool operator !=(AccountContact left, AccountContact right)
    {
        return !(left == right);
    }
}
