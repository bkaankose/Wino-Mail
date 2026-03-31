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
            recipient.Updated.Should().HaveCount(2);
            recipient.Updated[0].Source.Should().Be(MailUpdateSource.ClientUpdated);
            recipient.Updated[1].Source.Should().Be(MailUpdateSource.ClientReverted);
            recipient.Updated[1].UpdatedMail.IsRead.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public void ChangeFlagRequest_RevertUiChanges_RestoresOriginalFlagState()
    {
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
            recipient.Updated.Should().HaveCount(2);
            recipient.Updated[0].Source.Should().Be(MailUpdateSource.ClientUpdated);
            recipient.Updated[1].Source.Should().Be(MailUpdateSource.ClientReverted);
            recipient.Updated[1].UpdatedMail.IsFlagged.Should().BeFalse();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
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

    internal sealed class MailRequestRecipient : IRecipient<MailUpdatedMessage>
    {
        public List<MailUpdatedMessage> Updated { get; } = [];

        public void Receive(MailUpdatedMessage message) => Updated.Add(message);
    }
}
