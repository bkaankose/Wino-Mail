using System.Collections;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
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

    [Theory]
    [InlineData(true, MailOperation.MoveToOther, MailOperation.AlwaysMoveToOther)]
    [InlineData(false, MailOperation.MoveToFocused, MailOperation.AlwaysMoveToFocused)]
    public void GetMailItemContextMenuActions_ForOutlookInbox_OffersFocusedInboxActions(
        bool isFocused,
        MailOperation moveOperation,
        MailOperation alwaysMoveOperation)
    {
        var mail = CreateMail(isRead: true);
        mail.IsFocused = isFocused;
        mail.AssignedAccount = new MailAccount { ProviderType = MailProviderType.Outlook };

        var operations = _service.GetMailItemContextMenuActions([mail])
            .Select(action => action.Operation)
            .ToList();

        operations.Should().Contain(moveOperation);
        operations.Should().Contain(alwaysMoveOperation);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.IMAP4)]
    public void GetMailItemContextMenuActions_ForNonOutlookAccount_DoesNotOfferFocusedInboxActions(
        MailProviderType providerType)
    {
        var mail = CreateMail(isRead: true);
        mail.AssignedAccount = new MailAccount { ProviderType = providerType };

        var operations = _service.GetMailItemContextMenuActions([mail])
            .Select(action => action.Operation);

        operations.Should().NotContain(MailOperation.MoveToFocused);
        operations.Should().NotContain(MailOperation.MoveToOther);
        operations.Should().NotContain(MailOperation.AlwaysMoveToFocused);
        operations.Should().NotContain(MailOperation.AlwaysMoveToOther);
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
