using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels.Search;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MailSearchCriteriaFactoryTests
{
    private static readonly DateTime LocalNow = new(2026, 9, 23, 14, 30, 0, DateTimeKind.Local);

    [Theory]
    [InlineData(SearchBarReach.DownloadedOnly, SearchMode.Local, MailSearchReach.DownloadedOnly)]
    [InlineData(SearchBarReach.IncludeServer, SearchMode.Online, MailSearchReach.IncludeServer)]
    public void Create_MapsReachWithoutFilters(
        SearchBarReach reach,
        SearchMode expectedMode,
        MailSearchReach expectedReach)
    {
        var folderId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        var criteria = MailSearchCriteriaFactory.Create(
            "  roadmap  ", reach, MailSearchScope.CurrentFolder, MailSearchFilters.Empty, LocalNow, [folderId], [accountId]);

        criteria.Query.Should().Be("roadmap");
        criteria.ExecutionMode.Should().Be(expectedMode);
        criteria.Reach.Should().Be(expectedReach);
        criteria.Scope.Should().Be(MailSearchScope.CurrentFolder);
        criteria.FolderIds.Should().Equal(folderId);
        criteria.AccountIds.Should().Equal(accountId);
        criteria.HasFilters.Should().BeFalse();
        criteria.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(MailSearchScope.CurrentFolder)]
    [InlineData(MailSearchScope.Subfolders)]
    [InlineData(MailSearchScope.AllFolders)]
    public void Create_KeepsTheRequestedScope(MailSearchScope scope)
    {
        var criteria = MailSearchCriteriaFactory.Create("q", SearchBarReach.DownloadedOnly, scope, MailSearchFilters.Empty, LocalNow, [], []);

        criteria.Scope.Should().Be(scope);
    }

    [Fact]
    public void Create_MapsEveryFilter()
    {
        var filters = new MailSearchFilters(
            " elif ", " roadmap ", MailSearchDateRange.LastSevenDays, null, null, MailReadStatusFilter.Unread, true, true);

        var criteria = MailSearchCriteriaFactory.Create(string.Empty, SearchBarReach.IncludeServer, MailSearchScope.AllFolders, filters, LocalNow, [], []);

        criteria.Sender.Should().Be("elif");
        criteria.Subject.Should().Be("roadmap");
        criteria.ReceivedAfterUtc.Should().Be(new DateTimeOffset(new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Local)));
        criteria.ReceivedBeforeUtc.Should().Be(new DateTimeOffset(new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Local)));
        criteria.ReadStatus.Should().Be(MailReadStatusFilter.Unread);
        criteria.HasAttachments.Should().BeTrue();
        criteria.IsFlagged.Should().BeTrue();
        criteria.IsActive.Should().BeTrue("filters alone start a search");
    }

    [Fact]
    public void Filters_CustomRangeIncludesBothDays()
    {
        var filters = MailSearchFilters.Empty with
        {
            DateRange = MailSearchDateRange.Custom,
            CustomStartDate = new DateTime(2026, 9, 1),
            CustomEndDate = new DateTime(2026, 9, 3),
        };

        var (after, before) = filters.ResolveUtcRange(LocalNow);

        after.Should().Be(new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local)));
        before.Should().Be(new DateTimeOffset(new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Local)));
        filters.ActiveCount.Should().Be(1);
    }

    [Fact]
    public void Filters_CustomRangeWithoutDatesIsNotAFilter()
    {
        var filters = MailSearchFilters.Empty with { DateRange = MailSearchDateRange.Custom };

        filters.ActiveCount.Should().Be(0);
        filters.ResolveUtcRange(LocalNow).Should().Be(((DateTimeOffset?)null, (DateTimeOffset?)null));
    }

    [Fact]
    public void Chips_ListEachActiveFilterAndRemoveOne()
    {
        var filters = new MailSearchFilters(
            "elif", "roadmap", MailSearchDateRange.Today, null, null, MailReadStatusFilter.Read, true, true);

        var chips = MailSearchFilterChip.From(filters);

        chips.Select(chip => chip.Kind).Should().Equal(
            MailSearchFilterKind.Sender,
            MailSearchFilterKind.Subject,
            MailSearchFilterKind.Date,
            MailSearchFilterKind.ReadStatus,
            MailSearchFilterKind.Attachments,
            MailSearchFilterKind.Flagged);

        var withoutDate = MailSearchFilterChip.Remove(filters, MailSearchFilterKind.Date);
        withoutDate.DateRange.Should().Be(MailSearchDateRange.AnyTime);
        withoutDate.ActiveCount.Should().Be(5);
    }

    [Fact]
    public void Editor_RoundTripsFiltersAndResetKeepsScopeAndKeywords()
    {
        var filters = new MailSearchFilters(
            "elif", "roadmap", MailSearchDateRange.Custom, new DateTime(2026, 9, 1), new DateTime(2026, 9, 3), MailReadStatusFilter.Unread, true, false);
        var editor = new MailSearchFilterEditor();

        editor.Load(MailSearchScope.Subfolders, "q4", filters, isLocalSearch: false);

        editor.ToFilters().Should().Be(filters);
        editor.Scope.Should().Be(MailSearchScope.Subfolders);
        editor.IsCustomDateRange.Should().BeTrue();
        editor.IsLocalSearch.Should().BeFalse();

        editor.ResetCommand.Execute(null);

        editor.ToFilters().Should().Be(MailSearchFilters.Empty);
        editor.Scope.Should().Be(MailSearchScope.Subfolders);
        editor.Keywords.Should().Be("q4");
    }

    [Theory]
    [InlineData(MailReadStatusFilter.All, true, true)]
    [InlineData(MailReadStatusFilter.Unread, false, true)]
    [InlineData(MailReadStatusFilter.Read, true, false)]
    public void Matcher_AppliesReadStatus(MailReadStatusFilter status, bool matchesRead, bool matchesUnread)
    {
        bool Matches(bool isRead) => MailSearchFilterMatcher.Matches(
            new MailCopy { IsRead = isRead, CreationDate = DateTime.UtcNow },
            string.Empty, string.Empty, null, null, status, false, false);

        Matches(true).Should().Be(matchesRead);
        Matches(false).Should().Be(matchesUnread);
    }
}
