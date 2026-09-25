using System;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers;
using FluentAssertions;
using Moq;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// What a successful send leaves behind to tidy.
///
/// Every provider asks this the same question after its send has gone, and the three wrong answers
/// each have a visible cost. Leave a never-uploaded draft's local row and it sits in Drafts for good,
/// because there is no server copy for a sync to notice has gone. Hand a local draft to the provider
/// to delete on the server and it asks for an id that does not exist - on IMAP that threw after SMTP
/// had already sent, reporting the send as failed so the user sent it again. And throw from here at
/// all and the same thing happens: a send that went out is reported as one that did not.
/// </summary>
public class SentDraftCleanupTests
{
    private static readonly Guid Account = Guid.NewGuid();

    private static MailCopy Draft(bool local) => new()
    {
        UniqueId = Guid.NewGuid(),
        Id = local ? Guid.NewGuid().ToString() : "server-id",
        DraftId = local ? Constants.LocalDraftStartPrefix + Guid.NewGuid() : "server-draft-id",
    };

    [Fact]
    public async Task ADraftThatReachedTheServerIsHandedBackForTheProviderToDelete()
    {
        var processor = new Mock<IDefaultChangeProcessor>();
        var sent = Draft(local: false);

        var left = await SentDraftCleanup.ResolveAsync(processor.Object, Account, sent);

        left.Should().BeSameAs(sent);
        processor.Verify(p => p.DiscardLocalDraftAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never,
            "a server draft is the provider's to delete, not a local row to discard");
    }

    [Fact]
    public async Task ADraftThatNeverReachedTheServerIsDiscardedLocallyAndLeavesNothingToDelete()
    {
        var sent = Draft(local: true);
        var processor = new Mock<IDefaultChangeProcessor>();
        processor.Setup(p => p.DiscardLocalDraftAsync(Account, sent.UniqueId)).ReturnsAsync((MailCopy)null);

        var left = await SentDraftCleanup.ResolveAsync(processor.Object, Account, sent);

        left.Should().BeNull("there is no server copy, and asking a provider to delete one is what failed IMAP sends");
        processor.Verify(p => p.DiscardLocalDraftAsync(Account, sent.UniqueId), Times.Once);
    }

    [Fact]
    public async Task ACreateThatMappedTheDraftFirstLeavesItsServerCopyToDelete()
    {
        // The send was built from the local snapshot, but the upload finished before the tidy-up ran,
        // so a copy now exists on the server as well. The discard hands it back under the draft lock.
        var sent = Draft(local: true);
        var mapped = Draft(local: false);
        var processor = new Mock<IDefaultChangeProcessor>();
        processor.Setup(p => p.DiscardLocalDraftAsync(Account, sent.UniqueId)).ReturnsAsync(mapped);

        var left = await SentDraftCleanup.ResolveAsync(processor.Object, Account, sent);

        left.Should().BeSameAs(mapped);
    }

    [Fact]
    public async Task AFailureToDiscardDoesNotFailASendThatHasAlreadyGone()
    {
        var sent = Draft(local: true);
        var processor = new Mock<IDefaultChangeProcessor>();
        processor.Setup(p => p.DiscardLocalDraftAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
                 .ThrowsAsync(new InvalidOperationException("database is busy"));

        var resolve = async () => await SentDraftCleanup.ResolveAsync(processor.Object, Account, sent);

        await resolve.Should().NotThrowAsync("throwing here reports a delivered message as failed, and it gets sent twice");
    }

    [Fact]
    public async Task NoDraftMeansNothingToDo()
        => (await SentDraftCleanup.ResolveAsync(new Mock<IDefaultChangeProcessor>().Object, Account, null)).Should().BeNull();
}
