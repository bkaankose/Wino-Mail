using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi;
using Wino.Mapi.Rops;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Contacts over MAPI: the row to local-contact mapping and the property set written back.</summary>
public class MapiContactsTests
{
    private static readonly MapiContactTags Tags = new(
        0x8001001F, 0x8002001F, 0x8003001F, 0x8004001F, 0x8005001F, 0x8006001F,
        0x800D001F, 0x800E001F, 0x800F001F, 0x8010001F, 0x8007001F,
        0x8008001F, 0x8009001F, 0x800A001F, 0x800B001F, 0x800C001F);

    [Fact]
    public void ResolveSmtpAddress_InternalRecipient_UsesOriginalDisplayNameThenDisplayText()
    {
        // Internal recipients store the legacy DN in PidLidEmailNEmailAddress; the SMTP address sits in
        // PidLidEmailNOriginalDisplayName, and failing that inside the display text's parentheses.
        const string dn = "/o=Contoso/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=4dd262d9-Matthew Johnson";
        MapiContactOperations.ResolveSmtpAddress(dn, "EX", "matt@example.com").Should().Be("matt@example.com");
        MapiContactOperations.ResolveSmtpAddress(dn, "EX", null, "Matthew Johnson (matt@example.com)").Should().Be("matt@example.com");
        MapiContactOperations.ResolveSmtpAddress(dn, null, null, "Matthew Johnson").Should().BeNull();
        MapiContactOperations.ResolveSmtpAddress("ada@example.com", "SMTP", "ada@example.com").Should().Be("ada@example.com");
        MapiContactOperations.ResolveSmtpAddress(null, null, null).Should().BeNull();
    }

    [Fact]
    public void Row_MapsToAccountContact_WithFallbacksAndStructuredAddress()
    {
        var accountId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        var row = new MapiContactInfo(
            0x1234, "IPM.Contact",
            DisplayName: null, GivenName: "Ada", Surname: "Lovelace",
            Company: "Analytical Engines", JobTitle: " Engineer ",
            BusinessPhone: "+1 555 0100", HomePhone: null, MobilePhone: "", BusinessFax: null,
            Email1: null, Email2: "ada@example.com", Email3: null,
            WorkStreet: "1 Engine St", WorkCity: "London", WorkState: null, WorkPostalCode: "SW1", WorkCountry: "UK",
            Notes: "notes", HasAttachments: true);

        var contact = MapiContactMapper.ToAccountContact(row, "mapi:0000000000001234", accountId, bookId);

        contact.RemoteId.Should().Be("mapi:0000000000001234");
        contact.MailAccountId.Should().Be(accountId);
        contact.AddressBookId.Should().Be(bookId);
        contact.SourceKind.Should().Be(ContactSourceKind.Exchange);
        contact.RemotePhotoKey.Should().Be(MapiContactMapper.PhotoKey);
        contact.PrimaryEmailAddress.Should().Be("ada@example.com");
        contact.EmailAddresses.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
        contact.DisplayName.Should().Be("Ada Lovelace");
        contact.GivenName.Should().Be("Ada");
        contact.JobTitle.Should().Be("Engineer");
        contact.PhoneNumbers.Should().ContainSingle().Which.Kind.Should().Be(ContactPhoneKind.Work);
        var address = contact.PostalAddresses.Should().ContainSingle().Subject;
        address.Kind.Should().Be(ContactPostalAddressKind.Business);
        address.Street.Should().Be("1 Engine St");
        address.Region.Should().BeNull();
        address.Country.Should().Be("UK");
        contact.PendingMutation.Should().Be(ContactPendingMutation.None);
        row.IsContact.Should().BeTrue();
        new MapiContactInfo(1, "IPM.DistList", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, false).IsContact.Should().BeFalse();
    }

    [Fact]
    public void Write_ProducesClassNamesFileAsAndEmail1Triplet()
    {
        var item = new AccountContact { DisplayName = "Ada", CompanyName = "AE", Notes = "n" };
        item.Address = "ada@example.com";
        item.PhoneNumbers.Add(new ContactPhoneNumber { ContactId = item.Id, Number = "5", Kind = ContactPhoneKind.Mobile });

        var write = MapiContactMapper.ToWrite(item);
        var values = MapiContactOperations.Properties(Tags, write, includeClass: true);

        values.Should().Contain(v => v.Tag == PropertyTags.MessageClass && (string)v.Value == "IPM.Contact");
        values.Should().Contain(v => v.Tag == PropertyTags.DisplayName && (string)v.Value == "Ada");
        values.Should().Contain(v => v.Tag == Tags.FileUnder && (string)v.Value == "Ada");
        values.Should().Contain(v => v.Tag == Tags.Email1 && (string)v.Value == "ada@example.com");
        values.Should().Contain(v => v.Tag == Tags.Email1AddressType && (string)v.Value == "SMTP");
        values.Should().Contain(v => v.Tag == Tags.Email1DisplayName && (string)v.Value == "Ada (ada@example.com)");
        values.Should().Contain(v => v.Tag == PropertyTags.MobileTelephoneNumber);
        values.Should().NotContain(v => v.Tag == PropertyTags.HomeTelephoneNumber);
        values.Should().Contain(v => v.Tag == PropertyTags.Body && (string)v.Value == "n");

        MapiContactOperations.Properties(Tags, write, includeClass: false).Should().NotContain(v => v.Tag == PropertyTags.MessageClass);
    }

    [Fact]
    public void Write_FallsBackToTheAddressAsDisplayName()
    {
        var item = new AccountContact();
        item.Address = "no-name@example.com";

        MapiContactMapper.ToWrite(item).DisplayName.Should().Be("no-name@example.com");
    }
}
