using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MailRequestStateTests
{
    [Fact]
    public void MarkReadRequest_RevertUiChanges_RestoresOriginalReadState()
    {
        WeakReferenceMessenger.Default.Reset();

        var mailCopy = CreateMailCopy(isRead: false, isFlagged: false);
        var request = new MarkReadRequest(mailCopy, IsRead: true);
        var recipient = new MailRequestRecipient();

        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            request.IsNoOp.Should().BeFalse();

            request.ApplyUIChanges();
            request.RevertUIChanges();

            mailCopy.IsRead.Should().BeFalse();
            recipient.StateUpdates.Should().HaveCount(2);
            recipient.StateUpdates[0].Source.Should().Be(EntityUpdateSource.ClientUpdated);
            recipient.StateUpdates[1].Source.Should().Be(EntityUpdateSource.ClientReverted);
            recipient.StateUpdates[1].UpdatedState.IsRead.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            WeakReferenceMessenger.Default.Reset();
        }
    }

    [Fact]
    public void ChangeFlagRequest_RevertUiChanges_RestoresOriginalFlagState()
    {
        WeakReferenceMessenger.Default.Reset();

        var mailCopy = CreateMailCopy(isRead: true, isFlagged: false);
        var request = new ChangeFlagRequest(mailCopy, IsFlagged: true);
        var recipient = new MailRequestRecipient();

        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            request.IsNoOp.Should().BeFalse();

            request.ApplyUIChanges();
            request.RevertUIChanges();

            mailCopy.IsFlagged.Should().BeFalse();
            recipient.StateUpdates.Should().HaveCount(2);
            recipient.StateUpdates[0].Source.Should().Be(EntityUpdateSource.ClientUpdated);
            recipient.StateUpdates[1].Source.Should().Be(EntityUpdateSource.ClientReverted);
            recipient.StateUpdates[1].UpdatedState.IsFlagged.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            WeakReferenceMessenger.Default.Reset();
        }
    }

    private static MailCopy CreateMailCopy(bool isRead, bool isFlagged) =>
        new()
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString(),
            FolderId = Guid.NewGuid(),
            IsRead = isRead,
            IsFlagged = isFlagged
        };

    internal sealed class MailRequestRecipient : IRecipient<MailStateUpdatedMessage>
    {
        public List<MailStateUpdatedMessage> StateUpdates { get; } = [];

        public void Receive(MailStateUpdatedMessage message) => StateUpdates.Add(message);
    }
}
