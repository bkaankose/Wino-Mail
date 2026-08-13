using FluentAssertions;
using MailKit;
using MailKit.Search;
using MimeKit;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapSmtpMessageTests
{
    [Fact]
    public void CreateSmtpMessage_RemovesDraftHeaderWithoutMutatingOriginal()
    {
        var draftMessage = new MimeMessage
        {
            Subject = "Draft",
            Body = new TextPart("plain") { Text = "Body" }
        };
        draftMessage.Headers.Add(Constants.WinoLocalDraftHeader, "local-draft-id");

        var smtpMessage = ImapSynchronizer.CreateSmtpMessage(draftMessage);

        smtpMessage.Headers.Contains(Constants.WinoLocalDraftHeader).Should().BeFalse();
        draftMessage.Headers[Constants.WinoLocalDraftHeader].Should().Be("local-draft-id");
        smtpMessage.Subject.Should().Be(draftMessage.Subject);
        smtpMessage.TextBody.TrimEnd().Should().Be(draftMessage.TextBody);
    }

    [Fact]
    public async Task DeleteRemoteDraftIfPresentAsync_WhenProviderAlreadyRemovedDraft_DoesNotDeleteAnotherMessage()
    {
        var folder = new Mock<IMailFolder>(MockBehavior.Strict);
        folder.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var wasPresent = await ImapSynchronizer.DeleteRemoteDraftIfPresentAsync(folder.Object, new UniqueId(33));

        wasPresent.Should().BeFalse();
        folder.Verify(x => x.StoreAsync(
            It.IsAny<IList<UniqueId>>(),
            It.IsAny<IStoreFlagsRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        folder.Verify(x => x.ExpungeAsync(
            It.IsAny<IList<UniqueId>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteRemoteDraftIfPresentAsync_WhenDraftStillExists_DeletesOnlyItsUid()
    {
        var draftUid = new UniqueId(33);
        var folder = new Mock<IMailFolder>(MockBehavior.Strict);
        folder.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([draftUid]);
        folder.Setup(x => x.StoreAsync(
                It.Is<IList<UniqueId>>(uids => uids.SequenceEqual(new[] { draftUid })),
                It.Is<IStoreFlagsRequest>(request =>
                    request.Action == StoreAction.Add
                    && request.Flags == MessageFlags.Deleted
                    && request.Silent),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        folder.Setup(x => x.ExpungeAsync(
                It.Is<IList<UniqueId>>(uids => uids.SequenceEqual(new[] { draftUid })),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var wasPresent = await ImapSynchronizer.DeleteRemoteDraftIfPresentAsync(folder.Object, draftUid);

        wasPresent.Should().BeTrue();
        folder.VerifyAll();
    }
}
