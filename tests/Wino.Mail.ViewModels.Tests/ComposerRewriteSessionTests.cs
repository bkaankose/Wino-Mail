using System.Threading;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// The composer rewrite replaces the draft on screen, so every step must leave a way back to what
/// the user wrote.
/// </summary>
public sealed class ComposerRewriteSessionTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    [Fact]
    public async Task RefreshAvailability_FollowsTheCoordinatorGate()
    {
        var harness = new Harness(available: false);

        await harness.Session.RefreshAvailabilityAsync();
        harness.Session.IsAvailable.Should().BeFalse();

        harness.Available = true;
        await harness.Session.RefreshAvailabilityAsync();
        harness.Session.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAvailability_IsFalse_WithoutACoordinator()
    {
        var session = new ComposerRewriteSession(null, () => Task.FromResult<string?>("<p>x</p>"), _ => Task.CompletedTask, () => AccountId, _ => { });

        await session.RefreshAvailabilityAsync();

        session.IsAvailable.Should().BeFalse();
        session.IsPanelVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Rewrite_ReplacesTheDraft_AndSendsTheOriginalWithTheChosenMode()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>hi can we move the call</p>";
        harness.Result = "<p>Hello, could we reschedule the call?</p>";

        await harness.Session.RewriteCommand.ExecuteAsync("formal");

        harness.Coordinator.Verify(x => x.RewriteDraftAsync(AccountId, It.IsAny<Guid>(), "<p>hi can we move the call</p>", "formal", It.IsAny<CancellationToken>()), Times.Once);
        harness.Editor.Should().Be("<p>Hello, could we reschedule the call?</p>");
        harness.Session.HasResult.Should().BeTrue();
        harness.Session.IsShowingRewrite.Should().BeTrue();
        harness.Session.IsPanelVisible.Should().BeTrue();
        harness.Session.StatusText.Should().Be(string.Format(Translator.WinoIntelligence_RewriteAppliedFormat, Translator.Composer_AiRewriteFormal));
    }

    [Fact]
    public async Task ToggleOriginal_SwitchesBothWays_AndKeepsEditsToEachVersion()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Result = "<p>rewritten</p>";
        await harness.Session.RewriteCommand.ExecuteAsync("clearer");

        harness.Editor = "<p>rewritten, edited</p>";
        await harness.Session.ToggleOriginalCommand.ExecuteAsync(null);
        harness.Editor.Should().Be("<p>original</p>");
        harness.Session.IsShowingRewrite.Should().BeFalse();
        harness.Session.ToggleText.Should().Be(Translator.Composer_AiRewriteShowRewrite);

        await harness.Session.ToggleOriginalCommand.ExecuteAsync(null);
        harness.Editor.Should().Be("<p>rewritten, edited</p>");
        harness.Session.IsShowingRewrite.Should().BeTrue();
        harness.Session.ToggleText.Should().Be(Translator.WinoIntelligence_ShowOriginal);
    }

    [Fact]
    public async Task ASecondTone_IsAppliedToTheOriginal_NotToTheFirstRewrite()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Result = "<p>first</p>";
        await harness.Session.RewriteCommand.ExecuteAsync("formal");

        harness.Result = "<p>second</p>";
        await harness.Session.RewriteCommand.ExecuteAsync("shorter");

        harness.Coordinator.Verify(x => x.RewriteDraftAsync(AccountId, It.IsAny<Guid>(), "<p>original</p>", "shorter", It.IsAny<CancellationToken>()), Times.Once);
        harness.Editor.Should().Be("<p>second</p>");
    }

    [Fact]
    public async Task Regenerate_RepeatsTheLastMode()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Result = "<p>first</p>";
        await harness.Session.RewriteCommand.ExecuteAsync("happy");

        await harness.Session.RegenerateCommand.ExecuteAsync(null);

        harness.Coordinator.Verify(x => x.RewriteDraftAsync(AccountId, It.IsAny<Guid>(), "<p>original</p>", "happy", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Keep_EndsTheSession_WithTheRewriteInPlace()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Result = "<p>rewritten</p>";
        await harness.Session.RewriteCommand.ExecuteAsync("polite");

        harness.Session.KeepCommand.Execute(null);

        harness.Editor.Should().Be("<p>rewritten</p>");
        harness.Session.HasResult.Should().BeFalse();
        harness.Session.IsPanelVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Failure_LeavesTheDraftAlone_AndReportsTheError()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Error = "This email is too large to process with Wino AI.";

        await harness.Session.RewriteCommand.ExecuteAsync("formal");

        harness.Editor.Should().Be("<p>original</p>");
        harness.Renders.Should().Be(0);
        harness.Session.HasResult.Should().BeFalse();
        harness.Session.IsBusy.Should().BeFalse();
        harness.Errors.Should().Equal("This email is too large to process with Wino AI.");
    }

    [Fact]
    public async Task CanceledRequest_DoesNotTouchTheDraft()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        harness.Canceled = true;

        await harness.Session.RewriteCommand.ExecuteAsync("formal");

        harness.Editor.Should().Be("<p>original</p>");
        harness.Errors.Should().BeEmpty();
        harness.Session.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Reset_WhileRunning_DropsTheLateResult()
    {
        var harness = await Harness.CreateAvailableAsync();
        harness.Editor = "<p>original</p>";
        var gate = new TaskCompletionSource<WinoIntelligenceOperationResult<string>>();
        harness.Coordinator
            .Setup(x => x.RewriteDraftAsync(AccountId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        var running = harness.Session.RewriteCommand.ExecuteAsync("formal");
        harness.Session.IsBusy.Should().BeTrue();

        harness.Session.Reset();
        gate.SetResult(new WinoIntelligenceOperationResult<string>(Guid.NewGuid(), "draft", "<p>late</p>", false, null));
        await running;

        harness.Editor.Should().Be("<p>original</p>");
        harness.Session.HasResult.Should().BeFalse();
        harness.Coordinator.Verify(x => x.CancelRequest(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task Rewrite_DoesNothing_WhenUnavailable()
    {
        var harness = new Harness(available: false);
        await harness.Session.RefreshAvailabilityAsync();

        await harness.Session.RewriteCommand.ExecuteAsync("formal");

        harness.Coordinator.Verify(x => x.RewriteDraftAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness
    {
        public Harness(bool available)
        {
            Available = available;
            Coordinator
                .Setup(x => x.IsDraftRewriteAvailableAsync(AccountId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Available);
            Coordinator
                .Setup(x => x.RewriteDraftAsync(AccountId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid requestId, string _, string _, CancellationToken _) =>
                    new WinoIntelligenceOperationResult<string>(requestId, "draft", Error is null && !Canceled ? Result : null, Canceled, Error));

            Session = new ComposerRewriteSession(
                Coordinator.Object,
                () => Task.FromResult<string?>(Editor),
                html =>
                {
                    Renders++;
                    Editor = html;
                    return Task.CompletedTask;
                },
                () => AccountId,
                error => Errors.Add(error));
        }

        public static async Task<Harness> CreateAvailableAsync()
        {
            var harness = new Harness(available: true);
            await harness.Session.RefreshAvailabilityAsync();
            return harness;
        }

        public Mock<IWinoIntelligenceCoordinator> Coordinator { get; } = new();
        public ComposerRewriteSession Session { get; }
        public bool Available { get; set; }
        public string Editor { get; set; } = string.Empty;
        public string Result { get; set; } = "<p>rewritten</p>";
        public string? Error { get; set; }
        public bool Canceled { get; set; }
        public int Renders { get; private set; }
        public List<string> Errors { get; } = [];
    }
}
