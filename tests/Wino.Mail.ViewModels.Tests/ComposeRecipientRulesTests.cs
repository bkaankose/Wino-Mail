using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>The recipient rules the Mac, iPhone/iPad and Android composers share.</summary>
public sealed class ComposeRecipientRulesTests
{
    private static AccountContact Contact(string address, string? name = null) => new() { Address = address, DisplayName = name! };

    [Theory]
    [InlineData("ayse@example.com", "ayse@example.com", null)]
    [InlineData("  Ayşe Demir <ayse@example.com> ", "ayse@example.com", "Ayşe Demir")]
    public void TryParseTyped_ReadsTheAddressAndOptionalName(string typed, string address, string? name)
    {
        ComposeRecipientRules.TryParseTyped(typed, out var parsed, out var parsedName).Should().BeTrue();
        parsed.Should().Be(address);
        parsedName.Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ayse")]
    [InlineData(null)]
    public void TryParseTyped_RejectsTextWithoutAnAddress(string? typed)
        => ComposeRecipientRules.TryParseTyped(typed, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("ayse@example.com", true)]
    [InlineData("Ayşe <ayse@example.com>", true)]
    [InlineData("ayse@example", false)]
    [InlineData("ay", false)]
    public void IsCompleteAddress_NeedsAnAtAndADot(string typed, bool complete)
        => ComposeRecipientRules.IsCompleteAddress(typed).Should().Be(complete);

    [Theory]
    [InlineData("a", false)]
    [InlineData(" a ", false)]
    [InlineData("ay", true)]
    public void ShouldSuggest_StartsAtTwoCharacters(string query, bool suggest)
        => ComposeRecipientRules.ShouldSuggest(query).Should().Be(suggest);

    [Fact]
    public void ExcludeExisting_DropsPeopleAlreadyInTheField_ButKeepsLists()
    {
        var present = new RecipientSuggestion { Source = RecipientSuggestionSource.Contact };
        present.Address = "AYSE@example.com";
        var absent = new RecipientSuggestion { Source = RecipientSuggestionSource.History };
        absent.Address = "burak@example.com";
        var list = new RecipientSuggestion { Source = RecipientSuggestionSource.List, ListMembers = [Contact("ayse@example.com")] };

        var result = ComposeRecipientRules.ExcludeExisting([present, absent, list], [Contact("ayse@example.com")]);

        result.Should().Equal(absent, list);
    }

    [Fact]
    public void AddressesFor_ListAddsMembersNotAlreadyThere_Once()
    {
        var list = new RecipientSuggestion
        {
            Source = RecipientSuggestionSource.List,
            ListMembers = [Contact("ayse@example.com", "Ayşe"), Contact("burak@example.com", "Burak"), Contact("BURAK@example.com"), new AccountContact()]
        };

        var result = ComposeRecipientRules.AddressesFor(list, [Contact("ayse@example.com")]);

        result.Should().Equal(("burak@example.com", "Burak"));
    }

    [Fact]
    public void AddressesFor_SingleSuggestion_IsEmptyWhenAlreadyInTheField()
    {
        var suggestion = new RecipientSuggestion { Source = RecipientSuggestionSource.Contact, DisplayName = "Ayşe" };
        suggestion.Address = "ayse@example.com";

        ComposeRecipientRules.AddressesFor(suggestion, []).Should().Equal(("ayse@example.com", "Ayşe"));
        ComposeRecipientRules.AddressesFor(suggestion, [Contact("ayse@example.com")]).Should().BeEmpty();
    }
}
