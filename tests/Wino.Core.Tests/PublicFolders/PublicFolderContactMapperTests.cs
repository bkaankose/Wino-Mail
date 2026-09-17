using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.PublicFolders;
using Xunit;

namespace Wino.Core.Tests.PublicFolders;

public class PublicFolderContactMapperTests
{
    private readonly Guid _accountId = Guid.NewGuid();

    [Fact]
    public void Card_CarriesNameAddressCompanyAndPhones()
    {
        var card = PublicFolderContactMapper.ToAccountContact(new PublicFolderContact
        {
            RemoteId = "item-1",
            DisplayName = " Zoe Zimmer ",
            Address = "Zoe@Contoso.test",
            Company = "Contoso",
            Title = "Buyer",
            BusinessPhone = "555 0102",
            MobilePhone = "555 0103",
            HomePhone = " ",
            StreetAddress = "1 Main St",
            Notes = "VIP"
        }, _accountId, "staff");

        card.MailAccountId.Should().Be(_accountId);
        card.AddressBookId.Should().Be(Guid.Empty, "the card belongs to no stored address book");
        card.SourceKind.Should().Be(ContactSourceKind.Exchange);
        card.DisplayName.Should().Be("Zoe Zimmer");
        card.SortKey.Should().Be("Zoe Zimmer");
        card.PrimaryEmailAddress.Should().Be("Zoe@Contoso.test");
        card.EmailAddresses.Should().ContainSingle().Which.NormalizedAddress.Should().Be("ZOE@CONTOSO.TEST");
        card.CompanyName.Should().Be("Contoso");
        card.JobTitle.Should().Be("Buyer");
        card.Notes.Should().Be("VIP");
        card.PhoneNumbers.Select(phone => (phone.Number, phone.Kind)).Should().Equal(
            ("555 0102", ContactPhoneKind.Work),
            ("555 0103", ContactPhoneKind.Mobile));
        card.PrimaryPhoneNumber.Should().Be("555 0102");
        card.PostalAddresses.Should().ContainSingle().Which.Street.Should().Be("1 Main St");
    }

    [Fact]
    public void Identity_IsStablePerAccountFolderAndItem()
    {
        var contact = new PublicFolderContact { RemoteId = "item-1", DisplayName = "Zoe" };

        var first = PublicFolderContactMapper.ToAccountContact(contact, _accountId, "staff");
        var again = PublicFolderContactMapper.ToAccountContact(contact, _accountId, "staff");
        var otherFolder = PublicFolderContactMapper.ToAccountContact(contact, _accountId, "vendors");
        var otherAccount = PublicFolderContactMapper.ToAccountContact(contact, Guid.NewGuid(), "staff");

        again.Id.Should().Be(first.Id);
        otherFolder.Id.Should().NotBe(first.Id);
        otherAccount.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public void ANamelessContact_FallsBackToCompanyThenAddress()
    {
        PublicFolderContactMapper.ToAccountContact(new PublicFolderContact { RemoteId = "1", Company = "Fabrikam" }, _accountId, "f")
            .DisplayName.Should().Be("Fabrikam");
        PublicFolderContactMapper.ToAccountContact(new PublicFolderContact { RemoteId = "2", Address = "a@b.test" }, _accountId, "f")
            .DisplayName.Should().Be("a@b.test");
    }

    [Fact]
    public void Cards_AreSortedDeduplicatedAndSearchable()
    {
        var contacts = new[]
        {
            new PublicFolderContact { RemoteId = "2", DisplayName = "Zoe", Company = "Contoso" },
            null,
            new PublicFolderContact { RemoteId = "1", DisplayName = "adam", MobilePhone = "555 0101" },
            new PublicFolderContact { RemoteId = "2", DisplayName = "Zoe", Company = "Contoso" }
        };

        PublicFolderContactMapper.ToAccountContacts(contacts, _accountId, "staff")
            .Select(card => card.DisplayName).Should().Equal("adam", "Zoe");
        PublicFolderContactMapper.ToAccountContacts(contacts, _accountId, "staff", " conto ")
            .Select(card => card.DisplayName).Should().Equal("Zoe");
        PublicFolderContactMapper.ToAccountContacts(contacts, _accountId, "staff", "0101")
            .Select(card => card.DisplayName).Should().Equal("adam");
        PublicFolderContactMapper.ToAccountContacts(null, _accountId, "staff").Should().BeEmpty();
    }
}
