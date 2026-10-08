using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class WinoIntelligenceHeaderPresenterTests
{
    private const string Html = "<p>Hello</p>";

    private readonly Mock<IWinoIntelligenceCoordinator> _coordinator = new();
    private readonly Mock<IClipboardService> _clipboard = new();
    private readonly Mock<IMailDialogService> _dialogs = new();
    private readonly Mock<IPreferencesService> _preferences = new();

    public WinoIntelligenceHeaderPresenterTests()
    {
        _preferences.SetupProperty(p => p.AiDefaultTranslationLanguageCode, "en-US");
    }

    [Fact]
    public async Task Draft_IsHiddenAndNeverLoadsSnapshot()
    {
        var presenter = CreatePresenter();
        presenter.BeginMailItem(CreateItem(isDraft: true));

        await presenter.OnHtmlRendered(Html, "Subject", "a@b.c", DateTime.UtcNow);

        presenter.IsVisible.Should().BeFalse();
        _coordinator.Verify(c => c.GetSnapshotAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyHtml_IsHiddenAndNeverLoadsSnapshot()
    {
        var presenter = CreatePresenter();
        presenter.BeginMailItem(CreateItem());

        await presenter.OnHtmlRendered(string.Empty, "Subject", "a@b.c", DateTime.UtcNow);

        presenter.IsVisible.Should().BeFalse();
        _coordinator.Verify(c => c.GetSnapshotAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_IsAppliedToHeaderState()
    {
        SetupSnapshot(Snapshot(MailMessageIntelligenceState.Processed, cachedSummary: "Cached summary"));
        var item = CreateItem();
        var presenter = CreatePresenter();
        presenter.BeginMailItem(item);

        await presenter.OnHtmlRendered(Html, "Subject", "a@b.c", DateTime.UtcNow);

        presenter.IsVisible.Should().BeTrue();
        presenter.ContentKey.Should().Be($"{item.MailCopy.AssignedAccount.Id:N}:{item.MailCopy.UniqueId:N}");
        presenter.IsSummaryAvailable.Should().BeTrue();
        presenter.IsTranslateAvailable.Should().BeTrue();
        presenter.IsProcessingAvailable.Should().BeTrue();
        presenter.ProcessingState.Should().Be(WinoIntelligenceProcessingState.Processed);
        presenter.SummaryText.Should().Be("Cached summary");
        presenter.SummaryState.Should().Be(WinoIntelligenceFeatureState.Done);
    }

    [Fact]
    public async Task UnsupportedProcessingState_MapsToUnavailable()
    {
        SetupSnapshot(Snapshot(MailMessageIntelligenceState.Unsupported));
        var presenter = CreatePresenter();
        presenter.BeginMailItem(CreateItem());

        await presenter.OnHtmlRendered(Html, "Subject", "a@b.c", DateTime.UtcNow);

        presenter.ProcessingState.Should().Be(WinoIntelligenceProcessingState.Unavailable);
        presenter.CanRequestProcessing.Should().BeFalse();
    }

    [Fact]
    public async Task StaleSnapshot_ForPreviousMessage_IsIgnored()
    {
        var pending = new TaskCompletionSource<WinoIntelligenceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.Setup(c => c.GetSnapshotAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var presenter = CreatePresenter();
        presenter.BeginMailItem(CreateItem());
        var loading = presenter.OnHtmlRendered(Html, "Subject", "a@b.c", DateTime.UtcNow);

        presenter.BeginMailItem(CreateItem());
        pending.SetResult(Snapshot(MailMessageIntelligenceState.Processed, cachedSummary: "Old"));
        await loading;

        presenter.IsVisible.Should().BeFalse();
        presenter.SummaryText.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_GoesBusyThenDone()
    {
        var presenter = await CreateLoadedPresenterAsync();
        var pending = new TaskCompletionSource<WinoIntelligenceOperationResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = default;
        _coordinator.Setup(c => c.SummarizeAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback((WinoIntelligenceContext _, Guid id, CancellationToken _) => requestId = id)
            .Returns(pending.Task);
        (Guid Id, string Text)? completed = null;
        presenter.SummaryCompleted += (id, text) => completed = (id, text);

        var running = presenter.RequestSummaryCommand.ExecuteAsync(null);
        presenter.SummaryState.Should().Be(WinoIntelligenceFeatureState.Busy);

        pending.SetResult(new(requestId, presenter.ContentKey, "Short summary", false, null));
        await running;

        presenter.SummaryState.Should().Be(WinoIntelligenceFeatureState.Done);
        presenter.SummaryText.Should().Be("Short summary");
        completed.Should().Be((requestId, "Short summary"));
    }

    [Fact]
    public async Task SummaryFailure_ReturnsToIdleAndReportsError()
    {
        var presenter = await CreateLoadedPresenterAsync();
        _coordinator.Setup(c => c.SummarizeAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WinoIntelligenceContext _, Guid id, CancellationToken _) =>
                new WinoIntelligenceOperationResult<string>(id, presenter.ContentKey, null, false, "Quota exhausted"));
        Guid? failed = null;
        presenter.SummaryFailed += id => failed = id;

        await presenter.RequestSummaryCommand.ExecuteAsync(null);

        presenter.SummaryState.Should().Be(WinoIntelligenceFeatureState.Idle);
        failed.Should().NotBeNull();
        _dialogs.Verify(d => d.InfoBarMessage(It.IsAny<string>(), "Quota exhausted", InfoBarMessageType.Error), Times.Once);
    }

    [Fact]
    public async Task Translation_AppliesTogglesAndTranslatesAgainWithoutRequest()
    {
        var presenter = await CreateLoadedPresenterAsync();
        _coordinator.Setup(c => c.TranslateAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), null, "en-US", It.IsAny<CancellationToken>()))
            .ReturnsAsync((WinoIntelligenceContext _, Guid id, string? _, string _, CancellationToken _) =>
                new WinoIntelligenceOperationResult<MailTranslationResult>(id, presenter.ContentKey, new MailTranslationResult("de-DE", []), false, null));
        var rerenders = 0;
        presenter.RerenderRequested += () => { rerenders++; return Task.CompletedTask; };

        await presenter.TranslateCommand.ExecuteAsync(null);

        presenter.HasTranslationResult.Should().BeTrue();
        presenter.IsTranslationApplied.Should().BeTrue();
        presenter.IsShowingTranslation.Should().BeTrue();
        presenter.IsTranslationBusy.Should().BeFalse();
        presenter.TranslationStatusText.Should().Be("de-DE → en-US");
        rerenders.Should().Be(1);

        await presenter.TranslateCommand.ExecuteAsync(null);
        presenter.IsShowingTranslation.Should().BeFalse();
        presenter.IsTranslationApplied.Should().BeFalse();

        await presenter.TranslateCommand.ExecuteAsync(null);
        presenter.IsShowingTranslation.Should().BeTrue();
        presenter.IsTranslationApplied.Should().BeTrue();

        rerenders.Should().Be(3);
        _coordinator.Verify(c => c.TranslateAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _preferences.Object.AiDefaultTranslationLanguageCode.Should().Be("en-US");
    }

    [Fact]
    public async Task Translation_DetectSendsNoSourceLanguage()
    {
        var presenter = await CreateLoadedPresenterAsync();
        _coordinator.Setup(c => c.TranslateAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WinoIntelligenceContext _, Guid id, string? _, string _, CancellationToken _) =>
                new WinoIntelligenceOperationResult<MailTranslationResult>(id, presenter.ContentKey, new MailTranslationResult("fr-FR", []), false, null));

        presenter.TranslationLanguages[0].Code.Should().BeEmpty();
        presenter.SelectedSourceLanguage = presenter.TranslationLanguages[0].Code;
        presenter.SelectedTargetLanguage = "tr-TR";
        await presenter.TranslateCommand.ExecuteAsync(null);

        _coordinator.Verify(c => c.TranslateAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), null, "tr-TR", It.IsAny<CancellationToken>()), Times.Once);
        _preferences.Object.AiDefaultTranslationLanguageCode.Should().Be("tr-TR");
    }

    [Fact]
    public async Task Translation_CancelStopsBusyStateAndCancelsRequest()
    {
        var presenter = await CreateLoadedPresenterAsync();
        var pending = new TaskCompletionSource<WinoIntelligenceOperationResult<MailTranslationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid requestId = default;
        _coordinator.Setup(c => c.TranslateAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((WinoIntelligenceContext _, Guid id, string? _, string _, CancellationToken _) => requestId = id)
            .Returns(pending.Task);

        var running = presenter.TranslateCommand.ExecuteAsync(null);
        presenter.IsTranslationBusy.Should().BeTrue();

        presenter.CancelTranslationCommand.Execute(null);
        presenter.IsTranslationBusy.Should().BeFalse();
        presenter.TranslationStatusText.Should().Be(Translator.WinoIntelligence_TranslationCanceled);
        _coordinator.Verify(c => c.CancelRequest(requestId), Times.Once);

        pending.SetResult(new(requestId, presenter.ContentKey, null, true, null));
        await running;
        presenter.HasTranslationResult.Should().BeFalse();
        presenter.IsShowingTranslation.Should().BeFalse();
    }

    [Fact]
    public async Task EntitlementLoss_HidesHeaderAndCancelsContext()
    {
        var presenter = await CreateLoadedPresenterAsync();
        var contentKey = presenter.ContentKey;

        ((IRecipient<WinoIntelligenceEntitlementChanged>)presenter).Receive(new WinoIntelligenceEntitlementChanged(
            WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow)));

        presenter.IsVisible.Should().BeFalse();
        _coordinator.Verify(c => c.CancelContext(contentKey), Times.Once);
    }

    [Fact]
    public async Task VisibilityChange_ForAnotherAccount_IsIgnored()
    {
        var presenter = await CreateLoadedPresenterAsync();
        var tileChanges = 0;
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WinoIntelligenceHeaderPresenter.IntelligenceTiles)) tileChanges++;
        };

        ((IRecipient<IntelligenceVisibilityChanged>)presenter).Receive(new IntelligenceVisibilityChanged(Guid.NewGuid(), ["briefing"]));

        tileChanges.Should().Be(0);
    }

    [Fact]
    public async Task CopyVerificationCode_OnlyCopiesWhenCodeExists()
    {
        _clipboard.Setup(c => c.CopyTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Wino.Core.Domain.Models.Platform.PlatformOperationResult(PlatformOperationStatus.Succeeded));
        var presenter = CreatePresenter();

        await presenter.CopyVerificationCodeCommand.ExecuteAsync(null);
        _clipboard.Verify(c => c.CopyTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        presenter.VerificationCode = "123456";
        await presenter.CopyVerificationCodeCommand.ExecuteAsync(null);
        _clipboard.Verify(c => c.CopyTextAsync("123456", It.IsAny<CancellationToken>()), Times.Once);
    }

    private WinoIntelligenceHeaderPresenter CreatePresenter()
        => new(_coordinator.Object, Mock.Of<IMailService>(), _clipboard.Object, _preferences.Object, _dialogs.Object,
            Mock.Of<IMailContentProjector>(), new InlineDispatcher());

    private async Task<WinoIntelligenceHeaderPresenter> CreateLoadedPresenterAsync()
    {
        SetupSnapshot(Snapshot(MailMessageIntelligenceState.Processed));
        var presenter = CreatePresenter();
        presenter.BeginMailItem(CreateItem());
        await presenter.OnHtmlRendered(Html, "Subject", "a@b.c", DateTime.UtcNow);
        return presenter;
    }

    private void SetupSnapshot(WinoIntelligenceSnapshot snapshot)
        => _coordinator.Setup(c => c.GetSnapshotAsync(It.IsAny<WinoIntelligenceContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

    private static WinoIntelligenceSnapshot Snapshot(MailMessageIntelligenceState state, string? cachedSummary = null)
        => new(true, true, true, true, state, null, null, null, cachedSummary);

    private static MailItemViewModel CreateItem(bool isDraft = false)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "me@example.test",
            ProviderType = MailProviderType.IMAP4,
            Preferences = new MailAccountPreferences()
        };
        return new MailItemViewModel(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString("N"),
            IsDraft = isDraft,
            AssignedAccount = account
        });
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
