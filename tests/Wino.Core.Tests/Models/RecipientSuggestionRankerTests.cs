using FluentAssertions;
using Wino.Core.Domain.Models.Contacts;
using Xunit;

namespace Wino.Core.Tests.Models;

public class RecipientSuggestionRankerTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("jo", "John Smith", "jsmith@example.com", 3.0)]
    [InlineData("sm", "John Smith", "jsmith@example.com", 3.0)]
    [InlineData("js", "John Smith", "jsmith@example.com", 2.5)]
    [InlineData("mit", "John Smith", "jsmith@example.com", 1.5)]
    [InlineData("zz", "John Smith", "jsmith@example.com", 0)]
    [InlineData("", "John Smith", "jsmith@example.com", 0)]
    public void MatchScore_PrefersWordStartsOverSubstrings(string query, string name, string address, double expected)
        => RecipientSuggestionRanker.MatchScore(query, name, address).Should().Be(expected);

    [Fact]
    public void APrefixMatch_OutranksAFrequentSubstringMatch()
    {
        var prefix = Score("an", "Anna Lee", "anna@example.com", sent: 0, received: 1, daysAgo: 200);
        var substring = Score("an", "Joanne Park", "joanne@example.com", sent: 2, received: 2, daysAgo: 30);

        prefix.Should().BeGreaterThan(substring);
    }

    [Fact]
    public void SomeoneWrittenToOftenAndRecently_OutranksAnEqualMatchWithNoHistory()
    {
        var frequent = Score("mar", "Mark Otto", "mark@example.com", sent: 12, received: 20, daysAgo: 3);
        var stranger = Score("mar", "Maria Kent", "maria@example.com", sent: 0, received: 0, daysAgo: null);

        frequent.Should().BeGreaterThan(stranger);
    }

    [Fact]
    public void RecentCorrespondence_OutranksTheSameAmountLongAgo()
    {
        var recent = Score("sa", "Sam Hill", "sam@example.com", sent: 3, received: 3, daysAgo: 7);
        var old = Score("sa", "Sara Lin", "sara@example.com", sent: 3, received: 3, daysAgo: 720);

        recent.Should().BeGreaterThan(old);
    }

    [Fact]
    public void Favorites_AndContacts_GetABonus()
    {
        var baseScore = Score("le", "Leo Grant", "leo@example.com", 0, 0, null);
        var contact = Score("le", "Leo Grant", "leo@example.com", 0, 0, null, isContact: true);
        var favorite = Score("le", "Leo Grant", "leo@example.com", 0, 0, null, isContact: true, isFavorite: true);

        contact.Should().BeGreaterThan(baseScore);
        favorite.Should().BeGreaterThan(contact);
    }

    private static double Score(string query, string name, string address, int sent, int received, int? daysAgo, bool isContact = false, bool isFavorite = false)
        => RecipientSuggestionRanker.Score(
            query,
            name,
            address,
            sent,
            received,
            daysAgo is int days ? Now.AddDays(-days) : null,
            isContact,
            isFavorite,
            Now);
}
