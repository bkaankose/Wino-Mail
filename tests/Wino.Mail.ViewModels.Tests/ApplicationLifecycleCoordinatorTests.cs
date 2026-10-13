using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ApplicationLifecycleCoordinatorTests
{
    private readonly List<string> _calls = [];
    private readonly Mock<IWinoLogger> _logger = new();
    private readonly Mock<IApplicationConfiguration> _configuration = new();
    private readonly Mock<IApplicationRuntime> _runtime = new();
    private readonly Mock<INewThemeService> _theme = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IServiceProvider> _services = new();

    public ApplicationLifecycleCoordinatorTests()
    {
        _configuration.SetupGet(c => c.ApplicationDataFolderPath).Returns(Path.Combine("data", "root"));
        _logger.Setup(l => l.SetupLogger(It.IsAny<string>())).Callback(() => _calls.Add("logger"));
        _runtime.Setup(r => r.StartAsync(It.IsAny<CancellationToken>())).Callback(() => _calls.Add("runtime.start")).Returns(Task.CompletedTask);
        _runtime.Setup(r => r.StopAsync(It.IsAny<CancellationToken>())).Callback(() => _calls.Add("runtime.stop")).Returns(Task.CompletedTask);
        _theme.Setup(t => t.InitializeAsync()).Callback(() => _calls.Add("theme")).Returns(Task.CompletedTask);
        _services.Setup(s => s.GetService(typeof(IApplicationRuntime))).Callback(() => _calls.Add("resolve.runtime")).Returns(_runtime.Object);
        _services.Setup(s => s.GetService(typeof(INewThemeService))).Returns(_theme.Object);
        _services.Setup(s => s.GetService(typeof(IAccountService))).Returns(_accounts.Object);
    }

    private ApplicationLifecycleCoordinator CreateCoordinator() => new(_services.Object, _logger.Object, _configuration.Object);

    private void HasAccounts(bool any)
        => _accounts.Setup(a => a.GetAccountsAsync())
            .Callback(() => _calls.Add("accounts"))
            .ReturnsAsync(any ? [new MailAccount { Id = Guid.NewGuid() }] : []);

    private sealed class RecordingHost(List<string> calls, Func<Task>? servicesReady = null) : IApplicationLifecycleHost
    {
        public ApplicationStartupTarget? Shown { get; private set; }

        public Task ServicesReadyAsync()
        {
            calls.Add("hooks.ready");
            return servicesReady?.Invoke() ?? Task.CompletedTask;
        }

        public Task StoppingAsync(bool runtimeStarted)
        {
            calls.Add($"hooks.stopping({runtimeStarted})");
            return Task.CompletedTask;
        }

        public Task ShowStartupAsync(ApplicationStartupTarget target)
        {
            Shown = target;
            calls.Add("show." + target);
            return Task.CompletedTask;
        }

        public Task StopNavigationAsync()
        {
            calls.Add("navigation.stop");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true, ApplicationStartupTarget.Shell)]
    [InlineData(false, ApplicationStartupTarget.Welcome)]
    public async Task Start_FollowsTheSharedOrderAndPicksTheFirstWindow(bool hasAccounts, ApplicationStartupTarget expected)
    {
        HasAccounts(hasAccounts);
        var host = new RecordingHost(_calls);
        var coordinator = CreateCoordinator();

        var target = await coordinator.StartAsync(host);

        target.Should().Be(expected);
        host.Shown.Should().Be(expected);
        coordinator.RuntimeStarted.Should().BeTrue();
        _calls.Should().Equal("logger", "resolve.runtime", "runtime.start", "theme", "hooks.ready", "accounts", "show." + expected);
        _logger.Verify(l => l.SetupLogger(Path.Combine("data", "root", "Logs", "wino.log")), Times.Once);
    }

    [Fact]
    public async Task Start_MarksTheRuntimeStartedBeforeTheHooksRun()
    {
        HasAccounts(true);
        var coordinator = CreateCoordinator();
        bool? startedDuringHooks = null;
        var host = new RecordingHost(_calls, () =>
        {
            startedDuringHooks = coordinator.RuntimeStarted;
            return Task.CompletedTask;
        });

        await coordinator.StartAsync(host);

        startedDuringHooks.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_AfterStart_RunsHooksNavigationThenRuntime()
    {
        HasAccounts(true);
        var host = new RecordingHost(_calls);
        var coordinator = CreateCoordinator();
        await coordinator.StartAsync(host);
        _calls.Clear();

        await coordinator.StopAsync(host);

        _calls.Should().Equal("hooks.stopping(True)", "navigation.stop", "resolve.runtime", "runtime.stop");
    }

    [Fact]
    public async Task Stop_WhenTheRuntimeNeverStarted_LeavesItAlone()
    {
        _runtime.Setup(r => r.StartAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no database"));
        var host = new RecordingHost(_calls);
        var coordinator = CreateCoordinator();

        await FluentActions.Awaiting(() => coordinator.StartAsync(host)).Should().ThrowAsync<InvalidOperationException>();
        coordinator.RuntimeStarted.Should().BeFalse();
        _calls.Clear();

        await coordinator.StopAsync(host);

        _calls.Should().Equal("hooks.stopping(False)", "navigation.stop");
        _runtime.Verify(r => r.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
