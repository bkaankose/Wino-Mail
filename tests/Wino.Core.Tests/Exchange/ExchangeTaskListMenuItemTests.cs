using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.MenuItems;
using Xunit;

namespace Wino.Core.Tests.Exchange;

/// <summary>
/// The Exchange task list is the mailbox's Tasks folder. Renaming or deleting it would reach a
/// synchronizer path that refuses the request, so the pane must not offer either.
/// </summary>
public class ExchangeTaskListMenuItemTests
{
    [Theory]
    [InlineData(TaskSourceKind.Exchange, false, false)]
    [InlineData(TaskSourceKind.Gmail, true, true)]
    [InlineData(TaskSourceKind.Local, true, true)]
    public void ListActions_FollowTheSource(TaskSourceKind sourceKind, bool canRename, bool canDelete)
    {
        var item = new AccountTaskListMenuItem(
            new AccountTaskList { SourceKind = sourceKind, Title = "Tasks", IsDefault = true },
            "Account");

        item.CanRename.Should().Be(canRename);
        item.CanDelete.Should().Be(canDelete);
        item.CanMoveToGroup.Should().BeTrue();
    }

    [Fact]
    public void ReadOnlyList_OffersNeither()
    {
        var item = new AccountTaskListMenuItem(
            new AccountTaskList { SourceKind = TaskSourceKind.Gmail, IsReadOnly = true },
            "Account");

        item.CanRename.Should().BeFalse();
        item.CanDelete.Should().BeFalse();
    }
}
