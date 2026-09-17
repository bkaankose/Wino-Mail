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

    [Fact]
    public void GetMailItemContextMenuActions_WhenAnySelectedItemIsDraftOrSent_DisablesMove()
    {
        var inboxMail = CreateMail(isRead: true);
        var sentMail = CreateMail(isRead: true);
        sentMail.AssignedFolder.SpecialFolderType = SpecialFolderType.Sent;

        var moveAction = _service.GetMailItemContextMenuActions([inboxMail, sentMail])
            .Single(action => action.Operation == MailOperation.Move);

        moveAction.IsEnabled.Should().BeFalse();
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

    [Fact]
    public void GetMailItemContextMenuActions_ForMultipleOutlookInboxMails_OmitsAlwaysMoveActions()
    {
        var firstMail = CreateMail(isRead: true);
        firstMail.AssignedAccount = new MailAccount { ProviderType = MailProviderType.Outlook };
        firstMail.IsFocused = true;
        var secondMail = CreateMail(isRead: true);
        secondMail.AssignedAccount = firstMail.AssignedAccount;
        secondMail.IsFocused = true;

        var operations = _service.GetMailItemContextMenuActions([firstMail, secondMail])
            .Select(action => action.Operation);

        operations.Should().Contain(MailOperation.MoveToOther);
        operations.Should().NotContain(MailOperation.AlwaysMoveToFocused);
        operations.Should().NotContain(MailOperation.AlwaysMoveToOther);
    }

    [Theory]
    [InlineData(MailProviderType.Exchange, true)]
    [InlineData(MailProviderType.Outlook, false)]
    [InlineData(MailProviderType.Gmail, false)]
    [InlineData(MailProviderType.IMAP4, false)]
    public void GetMailItemContextMenuActions_OffersCreateRuleForExchangeOnly(MailProviderType providerType, bool expected)
    {
        var mail = CreateMail(isRead: true);
        mail.AssignedAccount = new MailAccount { ProviderType = providerType };

        var operations = _service.GetMailItemContextMenuActions([mail]).Select(action => action.Operation);

        (operations.Contains(MailOperation.CreateRule)).Should().Be(expected);
    }

    [Fact]
    public void GetMailItemContextMenuActions_OffersCreateRuleForASingleMessageOnly()
    {
        var first = CreateMail(isRead: true);
        first.AssignedAccount = new MailAccount { ProviderType = MailProviderType.Exchange };
        var second = CreateMail(isRead: true);
        second.AssignedAccount = first.AssignedAccount;

        _service.GetMailItemContextMenuActions([first, second])
            .Select(action => action.Operation)
            .Should().NotContain(MailOperation.CreateRule);
    }

    [Fact]
    public void GetMailItemContextMenuActions_OffersBlockAndNeverBlockOutsideJunk()
    {
        var mail = CreateMail(isRead: true);
        mail.AssignedAccount = new MailAccount { ProviderType = MailProviderType.Exchange };

        var operations = _service.GetMailItemContextMenuActions([mail]).Select(action => action.Operation).ToList();

        operations.Should().Contain(MailOperation.MoveToJunk);
        operations.Should().Contain(MailOperation.BlockSender);
        operations.Should().Contain(MailOperation.NeverBlockSender);
    }

    [Fact]
    public void GetMailItemContextMenuActions_InJunkOffersNeverBlockButNotBlock()
    {
        var mail = CreateMail(isRead: true);
        mail.AssignedFolder.SpecialFolderType = SpecialFolderType.Junk;
        mail.AssignedAccount = new MailAccount { ProviderType = MailProviderType.Gmail };

        var operations = _service.GetMailItemContextMenuActions([mail]).Select(action => action.Operation).ToList();

        operations.Should().Contain(MailOperation.MarkAsNotJunk);
        operations.Should().Contain(MailOperation.NeverBlockSender);
        operations.Should().NotContain(MailOperation.BlockSender);
    }

    [Fact]
    public void GetMailItemContextMenuActions_ForPop3_OffersNoJunkListActions()
    {
        var mail = CreateMail(isRead: true);
        mail.AssignedAccount = new MailAccount { ProviderType = MailProviderType.POP3 };

        var operations = _service.GetMailItemContextMenuActions([mail]).Select(action => action.Operation).ToList();

        operations.Should().NotContain(MailOperation.BlockSender);
        operations.Should().NotContain(MailOperation.NeverBlockSender);
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
