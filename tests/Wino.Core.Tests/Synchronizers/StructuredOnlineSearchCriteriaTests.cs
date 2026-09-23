using FluentAssertions;
using MailKit.Search;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class StructuredOnlineSearchCriteriaTests
{
    private static readonly DateTimeOffset After = new(2026, 8, 1, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Before = new(2026, 8, 12, 7, 0, 0, TimeSpan.Zero);

    private static readonly RemoteMailSearchCriteria Criteria = new(
        "roadmap",
        "alex@example.com",
        "Q4 review",
        After,
        Before,
        true,
        MailReadStatusFilter.Unread,
        true);

    private static readonly RemoteMailSearchCriteria FiltersOnly = new(
        string.Empty,
        string.Empty,
        string.Empty,
        After,
        null,
        true,
        MailReadStatusFilter.Read,
        true);

    [Fact]
    public void GmailQuery_MapsAllStructuredFilters()
    {
        var query = GmailSynchronizer.BuildOnlineSearchQuery(Criteria);

        query.Should().Contain("roadmap")
            .And.Contain("from:(alex@example.com)")
            .And.Contain("subject:(Q4 review)")
            .And.Contain($"after:{After.ToUnixTimeSeconds()}")
            .And.Contain($"before:{Before.ToUnixTimeSeconds()}")
            .And.Contain("has:attachment")
            .And.Contain("is:unread")
            .And.Contain("is:starred");
    }

    [Fact]
    public void GmailQuery_MapsReadMail()
        => GmailSynchronizer.BuildOnlineSearchQuery(FiltersOnly).Should().Contain("is:read").And.NotContain("is:unread");

    [Fact]
    public void OutlookSearch_MapsSearchablePropertiesOnly()
    {
        var query = OutlookSynchronizer.BuildOnlineSearchQuery(Criteria);

        query.Should().Contain("roadmap")
            .And.Contain("from:alex@example.com")
            .And.Contain("subject:\"Q4 review\"")
            .And.Contain("received>=2026-07-31")
            .And.Contain("received<2026-08-14")
            .And.Contain("hasAttachments:true");

        // Not $search properties. They are applied to the downloaded results.
        query.Should().NotContain("isRead").And.NotContain("flag");
    }

    [Fact]
    public void OutlookSearch_UsesFilterWhenThereIsNothingToSearchFor()
    {
        OutlookSynchronizer.HasOnlineSearchText(Criteria).Should().BeTrue();
        OutlookSynchronizer.HasOnlineSearchText(FiltersOnly).Should().BeFalse();

        var filter = OutlookSynchronizer.BuildOnlineFilterQuery(FiltersOnly);

        filter.Should().StartWith("receivedDateTime ge 2026-08-01T07:00:00Z")
            .And.Contain("isRead eq true")
            .And.Contain("flag/flagStatus eq 'flagged'")
            .And.Contain("hasAttachments eq true");
    }

    [Fact]
    public void OutlookSearchParameter_EscapesQuotesInsideTheQuery()
        => OutlookSynchronizer.ToGraphSearchParameter("subject:\"a b\" c\\d")
            .Should().Be("\"subject:\\\"a b\\\" c\\\\d\"");

    [Fact]
    public void ImapQuery_CombinesServerSearchableFilters()
    {
        var query = ImapSynchronizer.BuildOnlineSearchQuery(Criteria);
        var expected = SearchQuery.BodyContains("roadmap")
            .Or(SearchQuery.SubjectContains("roadmap"))
            .And(SearchQuery.FromContains("alex@example.com"))
            .And(SearchQuery.SubjectContains("Q4 review"))
            .And(SearchQuery.DeliveredAfter(new DateTime(2026, 7, 31)))
            .And(SearchQuery.DeliveredBefore(new DateTime(2026, 8, 14)))
            .And(SearchQuery.NotSeen)
            .And(SearchQuery.Flagged);

        // Attachments have no SEARCH key and are applied to the downloaded results.
        query.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void ImapQuery_MapsReadMail()
    {
        var query = ImapSynchronizer.BuildOnlineSearchQuery(FiltersOnly);
        var expected = SearchQuery.All
            .And(SearchQuery.DeliveredAfter(new DateTime(2026, 7, 31)))
            .And(SearchQuery.Seen)
            .And(SearchQuery.Flagged);

        query.Should().BeEquivalentTo(expected);
    }
}
