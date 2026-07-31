using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DraftSyncRetryServiceTests
{
    [Theory]
    [InlineData(0, 29, false)]
    [InlineData(0, 30, true)]
    [InlineData(1, 119, false)]
    [InlineData(1, 120, true)]
    [InlineData(2, 599, false)]
    [InlineData(2, 600, true)]
    [InlineData(3, 1799, false)]
    [InlineData(3, 1800, true)]
    [InlineData(4, 7199, false)]
    [InlineData(4, 7200, true)]
    public void IsEligibleForRetry_UsesAttemptBackoff(int attempts, int elapsedSeconds, bool expected)
    {
        var now = DateTime.UtcNow;
        var draft = CreateDraft(attempts, now.AddSeconds(-elapsedSeconds), DraftSyncState.SyncFailed);

        DraftSyncRetryService.IsEligibleForRetry(draft, MailProviderType.Outlook, now)
            .Should().Be(expected);
    }

    [Fact]
    public void IsEligibleForRetry_StopsAfterAttemptCap()
    {
        var draft = CreateDraft(
            DraftSyncRetryService.MaximumAttemptCount,
            DateTime.UtcNow.AddDays(-1),
            DraftSyncState.SyncFailed);

        DraftSyncRetryService.IsEligibleForRetry(draft, MailProviderType.Gmail, DateTime.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void IsEligibleForRetry_RequiresExplicitFailureForImap()
    {
        var draft = CreateDraft(0, null, DraftSyncState.PendingSync);

        DraftSyncRetryService.IsEligibleForRetry(draft, MailProviderType.IMAP4, DateTime.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public async Task QueueEligibleRetriesAsync_SkipsDraftWithPendingOperation()
    {
        var accountId = Guid.NewGuid();
        var draft = CreateDraft(0, null, DraftSyncState.SyncFailed);
        var mailService = new Mock<IMailService>();
        mailService.Setup(x => x.GetUnsyncedLocalDraftsAsync(accountId)).ReturnsAsync([draft]);

        var synchronizer = new Mock<IWinoSynchronizerBase>();
        synchronizer.SetupGet(x => x.Account).Returns(new MailAccount
        {
            Id = accountId,
            ProviderType = MailProviderType.Gmail
        });
        synchronizer.Setup(x => x.HasPendingOperation(draft.UniqueId)).Returns(true);

        var service = new DraftSyncRetryService(
            mailService.Object,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ISynchronizerFactory>());

        var queued = await service.QueueEligibleRetriesAsync(accountId, synchronizer.Object);

        queued.Should().BeFalse();
        synchronizer.Verify(x => x.QueueRequest(It.IsAny<IRequestBase>()), Times.Never);
        mailService.Verify(x => x.MarkDraftSyncAttemptAsync(It.IsAny<Guid>()), Times.Never);
    }

    private static MailCopy CreateDraft(int attempts, DateTime? lastAttempt, DraftSyncState state) => new()
    {
        UniqueId = Guid.NewGuid(),
        DraftId = $"localDraft_{Guid.NewGuid()}",
        IsDraft = true,
        DraftSyncAttemptCount = attempts,
        LastDraftSyncAttemptUtc = lastAttempt,
        DraftSyncState = state
    };
}
