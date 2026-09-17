using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.PublicFolders;
using Xunit;

namespace Wino.Core.Tests.PublicFolders;

public class RemoteMailPagerTests
{
    private static List<MailCopy> Window(int start, int count)
        => Enumerable.Range(start, count)
            .Select(index => new MailCopy { UniqueId = Guid.NewGuid(), Id = $"item-{index}" })
            .ToList();

    [Fact]
    public void Browsing_UsesTheSmallPageAndNarrowingTheLargeOne()
    {
        new RemoteMailPager(isNarrowedLocally: false).PageSize.Should().Be(RemoteMailPager.BrowsePageSize);
        new RemoteMailPager(isNarrowedLocally: true).PageSize.Should().Be(RemoteMailPager.FilteredPageSize);
    }

    [Fact]
    public void FullWindows_AdvanceTheOffsetAndKeepAskingForMore()
    {
        var pager = new RemoteMailPager(isNarrowedLocally: false);

        pager.Accept(Window(0, pager.PageSize)).Should().HaveCount(pager.PageSize);
        pager.Offset.Should().Be(pager.PageSize);
        pager.HasMore.Should().BeTrue();

        pager.Accept(Window(pager.PageSize, pager.PageSize)).Should().HaveCount(pager.PageSize);
        pager.Offset.Should().Be(pager.PageSize * 2);
        pager.HasMore.Should().BeTrue();
    }

    [Fact]
    public void AShortWindow_EndsTheListing()
    {
        var pager = new RemoteMailPager(isNarrowedLocally: false);

        pager.Accept(Window(0, 7)).Should().HaveCount(7);

        pager.Offset.Should().Be(7);
        pager.HasMore.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyWindow_EndsTheListing()
    {
        var pager = new RemoteMailPager(isNarrowedLocally: false);
        pager.Accept(Window(0, pager.PageSize));

        pager.Accept(new List<MailCopy>()).Should().BeEmpty();

        pager.HasMore.Should().BeFalse();
        pager.Offset.Should().Be(pager.PageSize);
    }

    [Fact]
    public void ItemsSeenBefore_AreDroppedByServerIdEvenWithANewLocalIdentity()
    {
        var pager = new RemoteMailPager(isNarrowedLocally: false);
        pager.Accept(Window(0, pager.PageSize));

        // A message delivered between two requests shifts the window by one, so its first row repeats.
        var shifted = Window(pager.PageSize - 1, pager.PageSize);
        var fresh = pager.Accept(shifted);

        fresh.Should().HaveCount(pager.PageSize - 1);
        fresh.Select(item => item.Id).Should().NotContain($"item-{pager.PageSize - 1}");
        pager.HasMore.Should().BeTrue();
    }
}
