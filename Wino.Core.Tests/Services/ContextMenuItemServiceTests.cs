using System.Collections;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ContextMenuItemServiceTests
{
    private readonly ContextMenuItemService _service = new();

    [Fact]
    public void GetMailItemContextMenuActions_EnumeratesSelectionOnce()
    {
        var selection = new SingleEnumerationSequence<MailCopy>(
        [
            CreateMail(isRead: true),
            CreateMail(isRead: false)
        ]);

        var actions = _service.GetMailItemContextMenuActions(selection).ToList();

        selection.EnumerationCount.Should().Be(1);
        actions.Select(action => action.Operation).Should().Contain(
        [
            MailOperation.MarkAsRead,
            MailOperation.MarkAsUnread
        ]);
    }

    [Fact]
    public void GetMailItemContextMenuActions_WithEmptySelection_ReturnsNoActions()
    {
        var actions = _service.GetMailItemContextMenuActions([]);

        actions.Should().BeEmpty();
    }

    private static MailCopy CreateMail(bool isRead) =>
        new()
        {
            UniqueId = Guid.NewGuid(),
            IsRead = isRead,
            AssignedFolder = new MailItemFolder
            {
                Id = Guid.NewGuid(),
                SpecialFolderType = SpecialFolderType.Inbox
            }
        };

    private sealed class SingleEnumerationSequence<T>(IEnumerable<T> items) : IEnumerable<T>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("Sequence was enumerated more than once.");
            }

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
