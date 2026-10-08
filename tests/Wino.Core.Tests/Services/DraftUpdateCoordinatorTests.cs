using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DraftUpdateCoordinatorTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static DraftUpdateSnapshot Snapshot(MailCopy mail, string subject) => new(mail.AssignedAccount.Id, mail.UniqueId,
        Encoding.UTF8.GetBytes($"From: me@example.test\r\nSubject: {subject}\r\n\r\nbody"));
    private static MailCopy Mail() => new()
    {
        UniqueId = Guid.NewGuid(), Id = "remote", DraftId = "draft", IsDraft = true,
        DraftSyncState = DraftSyncState.Synced,
        AssignedAccount = new MailAccount { Id = Guid.NewGuid(), ProviderType = MailProviderType.Gmail }
    };

    [Fact]
    public async Task Supersession_cancels_active_drops_intermediate_and_keeps_confirmed_identity()
    {
        var mail = Mail();
        var mails = new Mock<IMailService>();
        mails.Setup(x => x.GetSingleMailItemAsync(mail.UniqueId)).ReturnsAsync(mail);
        var calls = new ConcurrentQueue<string>();
        var started = Signal(); var canceled = Signal(); var unwind = Signal(); var completed = Signal();
        var manager = new Mock<ISynchronizationManager>(MockBehavior.Strict);
        manager.Setup(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(async (DraftUpdateSnapshot snapshot, CancellationToken token) =>
            {
                using var mime = snapshot.OpenMime();
                calls.Enqueue(mime.Subject);
                if (mime.Subject == "A")
                {
                    using var registration = token.Register(() => canceled.TrySetResult());
                    started.TrySetResult();
                    await unwind.Task;
                    return new DraftUpdateIdentity("accepted-A", "draft", "thread");
                }
                mail.Id.Should().Be("accepted-A");
                return new DraftUpdateIdentity("accepted-C", "draft", "thread");
            });
        mails.Setup(x => x.UpdateDraftIdentityAsync(mail.AssignedAccount.Id, mail.UniqueId, It.IsAny<DraftUpdateIdentity>()))
            .Callback<Guid, Guid, DraftUpdateIdentity>((_, _, identity) =>
            {
                identity.Apply(mail);
                if (identity.MessageId == "accepted-C") completed.TrySetResult();
            }).Returns(Task.CompletedTask);
        var logger = new Mock<IWinoLogger>(MockBehavior.Strict);
        await using var coordinator = new DraftUpdateCoordinator(manager.Object, mails.Object, logger.Object, new());

        coordinator.Schedule(Snapshot(mail, "A"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Schedule(Snapshot(mail, "B"));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Schedule(Snapshot(mail, "C"));
        calls.Should().Equal("A");
        unwind.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Equal("A", "C");
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Save_during_initial_creation_waits_for_mapping_and_uses_latest_snapshot()
    {
        var mail = Mail(); mail.DraftSyncState = DraftSyncState.PendingSync; mail.DraftId = Wino.Core.Domain.Constants.LocalDraftStartPrefix + "pending";
        var mails = new Mock<IMailService>();
        var read = Signal(); var upload = Signal();
        mails.Setup(x => x.GetSingleMailItemAsync(mail.UniqueId)).ReturnsAsync(() => { read.TrySetResult(); return mail; });
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns((DraftUpdateSnapshot snapshot, CancellationToken _) =>
            {
                using var mime = snapshot.OpenMime(); mime.Subject.Should().Be("latest");
                upload.TrySetResult(); return Task.FromResult(DraftUpdateIdentity.From(mail));
            });
        var registry = new DraftUpdateRegistry();
        await using var coordinator = new DraftUpdateCoordinator(manager.Object, mails.Object, Mock.Of<IWinoLogger>(), registry);
        coordinator.Schedule(Snapshot(mail, "first"));
        await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Schedule(Snapshot(mail, "latest"));
        manager.Verify(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        mail.DraftSyncState = DraftSyncState.Synced; mail.DraftId = "remote-draft";
        registry.NotifyMapped(mail.AssignedAccount.Id, mail.UniqueId);
        await upload.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failure_logs_only_identifiers_and_retries_on_next_save()
    {
        var mail = Mail(); var failed = Signal(); var retried = Signal();
        var mails = new Mock<IMailService>();
        mails.Setup(x => x.GetSingleMailItemAsync(mail.UniqueId)).ReturnsAsync(mail);
        var manager = new Mock<ISynchronizationManager>();
        var attempt = 0;
        manager.Setup(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns((DraftUpdateSnapshot _, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1) throw new IOException("SECRET subject attachment body");
                retried.TrySetResult(); return Task.FromResult(DraftUpdateIdentity.From(mail));
            });
        var logger = new Mock<IWinoLogger>(MockBehavior.Strict);
        logger.Setup(x => x.LogDraftUpdateFailure(mail.AssignedAccount.Id, mail.UniqueId)).Callback(() => failed.TrySetResult());
        await using var coordinator = new DraftUpdateCoordinator(manager.Object, mails.Object, logger.Object, new());
        coordinator.Schedule(Snapshot(mail, "first"));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attempt.Should().Be(1);
        coordinator.Schedule(Snapshot(mail, "retry"));
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        logger.Verify(x => x.LogDraftUpdateFailure(mail.AssignedAccount.Id, mail.UniqueId), Times.Once);
        logger.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Independent_drafts_run_concurrently_and_stop_drains_only_target_draft()
    {
        var a = Mail(); var b = Mail();
        var mails = new Mock<IMailService>();
        mails.Setup(x => x.GetSingleMailItemAsync(a.UniqueId)).ReturnsAsync(a);
        mails.Setup(x => x.GetSingleMailItemAsync(b.UniqueId)).ReturnsAsync(b);
        var startedA = Signal(); var startedB = Signal(); var released = Signal();
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(async (DraftUpdateSnapshot snapshot, CancellationToken token) =>
            {
                (snapshot.UniqueId == a.UniqueId ? startedA : startedB).TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { if (snapshot.UniqueId == a.UniqueId) released.TrySetResult(); }
                return null!;
            });
        await using var coordinator = new DraftUpdateCoordinator(manager.Object, mails.Object, Mock.Of<IWinoLogger>(), new());
        coordinator.Schedule(Snapshot(a, "A")); coordinator.Schedule(Snapshot(b, "B"));
        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(5));
        (await coordinator.StopAsync(a.AssignedAccount.Id, a.UniqueId)).Should().BeSameAs(a);
        released.Task.IsCompleted.Should().BeTrue();
        manager.Verify(x => x.UpdateDraftAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Local_save_failure_does_not_schedule_remote_work()
    {
        var mail = Mail(); var mime = new Mock<IMimeFileService>(); var mails = new Mock<IMailService>();
        var coordinator = new Mock<IDraftUpdateCoordinator>(MockBehavior.Strict);
        var service = new DraftSaveService(mime.Object, mails.Object, coordinator.Object, new());
        (await service.SaveAsync(Snapshot(mail, "draft"), mail)).Should().BeFalse();
        coordinator.VerifyNoOtherCalls();
        mails.Verify(x => x.SaveDraftMetadataAsync(It.IsAny<Guid>(), It.IsAny<MailCopy>()), Times.Never);
    }

    [Fact]
    public async Task Metadata_failure_does_not_schedule_remote_work()
    {
        var mail = Mail(); var mime = new Mock<IMimeFileService>(); var mails = new Mock<IMailService>();
        mime.Setup(x => x.SaveDraftMimeMessageAsync(It.IsAny<Guid>(), It.IsAny<MimeKit.MimeMessage>(), It.IsAny<Guid>())).ReturnsAsync(true);
        mails.Setup(x => x.SaveDraftMetadataAsync(It.IsAny<Guid>(), It.IsAny<MailCopy>())).ThrowsAsync(new IOException());
        var coordinator = new Mock<IDraftUpdateCoordinator>(MockBehavior.Strict);
        var service = new DraftSaveService(mime.Object, mails.Object, coordinator.Object, new());
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(Snapshot(mail, "draft"), mail));
        coordinator.VerifyNoOtherCalls();
    }
}
