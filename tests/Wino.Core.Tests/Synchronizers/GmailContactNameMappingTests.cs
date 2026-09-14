using FluentAssertions;
using Google.Apis.PeopleService.v1.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Synchronizers;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class GmailContactNameMappingTests
{
    [Fact]
    public void MapToGoogleContact_DisplayNameWithoutStructuredName_UsesOnlyTheFreeFormName()
    {
        var contact = Contact("Prince", null, null);

        var person = GmailSynchronizer.MapToGoogleContact(contact);

        var name = person.Names.Should().ContainSingle().Which;
        name.UnstructuredName.Should().Be("Prince");
        name.GivenName.Should().BeNull();
        name.FamilyName.Should().BeNull();
    }

    [Fact]
    public void MergeCommonFields_DisplayNameOnlyEdit_UsesFreeFormNameAndPreservesStructuredName()
    {
        var original = Contact("Ada Lovelace", "Ada", "Lovelace");
        var desired = Contact("Countess of Lovelace", "Ada", "Lovelace");
        var person = GooglePerson("Ada Lovelace", "Ada", "Lovelace");

        GmailSynchronizer.MergeCommonFields(person, desired, original);

        var name = person.Names.Should().ContainSingle().Which;
        name.UnstructuredName.Should().Be("Countess of Lovelace");
        name.GivenName.Should().Be("Ada");
        name.FamilyName.Should().Be("Lovelace");
    }

    [Fact]
    public void MergeCommonFields_StructuredNameOnlyEdit_ClearsTheOldFreeFormName()
    {
        var original = Contact("Countess of Lovelace", "Ada", "Lovelace");
        var desired = Contact("Countess of Lovelace", "Augusta Ada", "Lovelace");
        var person = GooglePerson("Countess of Lovelace", "Ada", "Lovelace");

        GmailSynchronizer.MergeCommonFields(person, desired, original);

        var name = person.Names.Should().ContainSingle().Which;
        name.UnstructuredName.Should().BeNull();
        name.GivenName.Should().Be("Augusta Ada");
        name.FamilyName.Should().Be("Lovelace");
    }

    [Fact]
    public void MergeCommonFields_UnrelatedEdit_PreservesTheProviderFreeFormName()
    {
        var original = Contact("Countess of Lovelace", "Ada", "Lovelace");
        var desired = Contact("Countess of Lovelace", "Ada", "Lovelace");
        desired.Notes = "Updated notes";
        var person = GooglePerson("Countess of Lovelace", "Ada", "Lovelace");

        GmailSynchronizer.MergeCommonFields(person, desired, original);

        person.Names.Should().ContainSingle().Which.UnstructuredName.Should().Be("Countess of Lovelace");
    }

    [Fact]
    public void MapToGoogleContact_EmptyName_OmitsTheNameEntry()
    {
        var contact = Contact(null, null, null);

        var person = GmailSynchronizer.MapToGoogleContact(contact);

        person.Names.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Free-form 名前", "Provider formatted", "Free-form 名前")]
    [InlineData(null, "Provider formatted", "Provider formatted")]
    public void GetGoogleContactDisplayName_PrefersWritableFreeFormValue(
        string unstructuredName,
        string providerDisplayName,
        string expected)
    {
        var name = new Name { UnstructuredName = unstructuredName, DisplayName = providerDisplayName };

        GmailSynchronizer.GetGoogleContactDisplayName(name).Should().Be(expected);
    }

    private static AccountContact Contact(string displayName, string givenName, string surname)
        => new()
        {
            DisplayName = displayName,
            GivenName = givenName,
            Surname = surname
        };

    private static Person GooglePerson(string unstructuredName, string givenName, string surname)
        => new()
        {
            Names =
            [
                new Name
                {
                    UnstructuredName = unstructuredName,
                    DisplayName = $"{givenName} {surname}",
                    GivenName = givenName,
                    FamilyName = surname,
                    Metadata = new FieldMetadata { Primary = true }
                }
            ]
        };
}
