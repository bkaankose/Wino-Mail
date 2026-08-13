using FluentAssertions;
using MailKit.Search;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class StructuredOnlineSearchCriteriaTests
{
    private static readonly RemoteMailSearchCriteria Criteria = new(
        "roadmap",
        "alex@example.com",
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
        true,
        true,
        true);

    [Fact]
    public void GmailQuery_MapsAllStructuredFilters()
    {
        var query = GmailSynchronizer.BuildOnlineSearchQuery(Criteria);

        query.Should().Contain("roadmap")
            .And.Contain("from:(alex@example.com)")
            .And.Contain("after:2026/08/01")
            .And.Contain("before:2026/08/12")
            .And.Contain("has:attachment")
            .And.Contain("is:unread")
            .And.Contain("is:starred");
    }

    [Fact]
    public void OutlookQuery_MapsAllStructuredFilters()
    {
        var query = OutlookSynchronizer.BuildOnlineSearchQuery(Criteria);

        query.Should().Contain("roadmap")
            .And.Contain("from:alex@example.com")
            .And.Contain("received>=2026-08-01")
            .And.Contain("received<2026-08-12")
            .And.Contain("hasAttachments:true")
            .And.Contain("isRead:false")
            .And.Contain("flag:flagged");
    }

    [Fact]
    public void ImapQuery_CombinesAllStructuredFilters()
    {
        var query = ImapSynchronizer.BuildOnlineSearchQuery(Criteria);
        var expected = SearchQuery.BodyContains("roadmap")
            .Or(SearchQuery.SubjectContains("roadmap"))
            .And(SearchQuery.FromContains("alex@example.com"))
            .And(SearchQuery.DeliveredAfter(Criteria.ReceivedAfterUtc!.Value.UtcDateTime))
            .And(SearchQuery.DeliveredBefore(Criteria.ReceivedBeforeUtc!.Value.UtcDateTime))
            .And(SearchQuery.HeaderContains("Content-Disposition", "attachment"))
            .And(SearchQuery.NotSeen)
            .And(SearchQuery.Flagged);

        query.Should().BeEquivalentTo(expected);
    }
}
