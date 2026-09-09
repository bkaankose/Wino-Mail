using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MailListFilterRetentionTests
{
    [Theory]
    [InlineData(FilterOptionType.Unread, MailCopyChangeFlags.IsRead, true, false, true)]
    [InlineData(FilterOptionType.Unread, MailCopyChangeFlags.IsRead, false, false, false)]
    [InlineData(FilterOptionType.Flagged, MailCopyChangeFlags.IsFlagged, false, false, true)]
    [InlineData(FilterOptionType.Flagged, MailCopyChangeFlags.IsFlagged, false, true, false)]
    [InlineData(FilterOptionType.All, MailCopyChangeFlags.IsRead, true, false, false)]
    [InlineData(FilterOptionType.Files, MailCopyChangeFlags.IsFlagged, false, false, false)]
    public void ShouldRetainFilterMutation_OnlyRetainsRowsInvalidatedByActiveQuickFilter(
        FilterOptionType filterType,
        MailCopyChangeFlags changedProperties,
        bool isRead,
        bool isFlagged,
        bool expected)
    {
        var result = MailListPageViewModel.ShouldRetainFilterMutation(
            filterType,
            changedProperties,
            isRead,
            isFlagged);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(FilterOptionType.Unread, MailCopyChangeFlags.IsFlagged, true, false)]
    [InlineData(FilterOptionType.Flagged, MailCopyChangeFlags.IsRead, false, false)]
    public void ShouldRetainFilterMutation_RequiresMatchingStateChange(
        FilterOptionType filterType,
        MailCopyChangeFlags changedProperties,
        bool isRead,
        bool isFlagged)
    {
        var result = MailListPageViewModel.ShouldRetainFilterMutation(
            filterType,
            changedProperties,
            isRead,
            isFlagged);

        Assert.False(result);
    }
}
