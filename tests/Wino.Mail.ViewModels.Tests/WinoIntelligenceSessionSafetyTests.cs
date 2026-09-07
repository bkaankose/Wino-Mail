using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.ViewModels;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class WinoIntelligenceSessionSafetyTests
{
    [Fact]
    public async Task SignOutDuringRefresh_ClearsConsentAndRejectsLateAccountData()
    {
        using var cancellation = new CancellationTokenSource();
        var winoAccount = new WinoAccount { Id = Guid.NewGuid() };
        var session = new WinoAccountSession(winoAccount.Id, 1, cancellation.Token);
        var sessions = new Mock<IWinoAccountSessionService>();
        sessions.Setup(x => x.CaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(x => x.CommitAsync(session, It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (WinoAccountSession captured, Func<Task> apply, CancellationToken token) =>
            {
                if (captured.CancellationToken.IsCancellationRequested) return false;
                await apply();
                return true;
            });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WinoAccountIntelligenceRefreshResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new Mock<IWinoAccountIntelligenceSnapshotService>();
        snapshots.Setup(x => x.RefreshAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            started.SetResult();
            return release.Task;
        });
        var viewModel = new WinoIntelligenceManagementPageViewModel(Mock.Of<IMailDialogService>(),
            Mock.Of<IAccountService>(), Mock.Of<IFolderService>(), Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(), Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<ILocalIntelligenceStore>(), Mock.Of<ITranslationService>(), Mock.Of<IIntelligenceCoverageHandoff>(),
            snapshotService: snapshots.Object, sessions: sessions.Object)
        {
            Account = new MailAccount { Id = Guid.NewGuid(), Address = "mail@example.test" },
            HasAccountConsent = true,
            IsQuotaAvailable = true
        };

        var pending = viewModel.RetryRemoteRefreshCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        viewModel.Receive(new WinoAccountProfileDeletedMessage(winoAccount));
        var stale = WinoAccountIntelligenceSnapshot.Empty(winoAccount.Id) with
        {
            Consent = new IntelligenceConsentDto(ConsentStatuses.Active, "v1", "v1",
                DateTimeOffset.UtcNow, null, "https://example.test/privacy", IntelligenceDeletionStatuses.NotRequired)
        };
        release.SetResult(new(stale, true, null));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.HasAccountConsent.Should().BeFalse();
        viewModel.IsQuotaAvailable.Should().BeFalse();
        viewModel.SemanticMailboxId.Should().BeNull();
    }
}
