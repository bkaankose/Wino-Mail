using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class NotificationSettingsPushRelayTests
{
    private static readonly PlatformCapabilities Mac = new(RemoteNotifications: true, PushRelayOptIn: true);
    private static readonly PlatformCapabilities Phone = new(RemoteNotifications: true);
    private static readonly PlatformCapabilities Windows = new(PushRelayOptIn: true);

    private readonly Mock<IPushRelayRegistrationService> _relay = new();
    private readonly Mock<IAccountService> _accounts = new();

    public NotificationSettingsPushRelayTests()
    {
        _accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount>());
        _relay.Setup(x => x.SetUserOptInAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public static TheoryData<PushRelayAvailability, string> StatusTexts => new()
    {
        { PushRelayAvailability.UnsupportedPlatform, Translator.NotificationSettings_PushRelay_PlatformUnsupported },
        { PushRelayAvailability.RejectedByRelay, Translator.NotificationSettings_PushRelay_PlatformUnsupported },
        { PushRelayAvailability.SignInRequired, Translator.NotificationSettings_PushRelay_SignInRequired },
        { PushRelayAvailability.DisabledByUser, Translator.NotificationSettings_PushRelay_Off },
        { PushRelayAvailability.ServiceUnavailable, Translator.NotificationSettings_PushRelay_ServiceUnavailable },
        { PushRelayAvailability.Enabled, string.Format(Translator.NotificationSettings_PushRelay_RegisteredFormat, 1, 10) }
    };

    [Theory]
    [MemberData(nameof(StatusTexts))]
    public void StatusText_FollowsAvailability(PushRelayAvailability availability, string expected)
    {
        var vm = Create(Mac, Snapshot(availability, optedIn: true, Watched()));

        vm.PushRelayStatusText.Should().Be(expected);
        vm.IsPushRelaySignInRequired.Should().Be(availability == PushRelayAvailability.SignInRequired);
    }

    [Fact]
    public void StatusText_UsesEnglishWording()
    {
        var vm = Create(Mac, Snapshot(PushRelayAvailability.Enabled, optedIn: true, Watched(), Watched()));

        vm.PushRelayStatusText.Should().Be("2 of 10 mailboxes watched");
    }

    [Fact]
    public void LimitReached_IsShownInsteadOfTheCount()
    {
        var limited = new PushRelayMailboxSnapshot(Guid.NewGuid(), null, null, false, null, PushRelayErrorCodes.MailboxLimitReached);
        var vm = Create(Phone, Snapshot(PushRelayAvailability.Enabled, optedIn: false, Watched(), limited));

        vm.PushRelayStatusText.Should().Be(Translator.NotificationSettings_PushRelay_LimitReached);
    }

    [Fact]
    public void OptInPlatform_ShowsTheToggleAndReflectsTheChoice()
    {
        var vm = Create(Mac, Snapshot(PushRelayAvailability.DisabledByUser, optedIn: false));

        vm.IsPushRelaySectionVisible.Should().BeTrue();
        vm.IsPushRelayToggleVisible.Should().BeTrue();
        vm.IsPushRelayEnabled.Should().BeFalse();
        vm.IsPushRelayToggleEnabled.Should().BeTrue();
        _relay.Verify(x => x.SetUserOptInAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TurningTheToggleOn_StoresTheOptIn()
    {
        var vm = Create(Windows, Snapshot(PushRelayAvailability.DisabledByUser, optedIn: false));

        vm.IsPushRelayEnabled = true;

        _relay.Verify(x => x.SetUserOptInAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SignedOut_DisablesTheToggle()
    {
        var vm = Create(Windows, Snapshot(PushRelayAvailability.SignInRequired, optedIn: false));

        vm.IsPushRelayToggleEnabled.Should().BeFalse();
        vm.IsPushRelaySignInRequired.Should().BeTrue();
    }

    [Fact]
    public void Phone_HidesTheToggleAndShowsTheTestButtonOnceRegistered()
    {
        var vm = Create(Phone, Snapshot(PushRelayAvailability.Enabled, optedIn: false, Watched()) with { DeviceId = Guid.NewGuid() });

        vm.IsPushRelayToggleVisible.Should().BeFalse();
        vm.IsPushRelayEnabled.Should().BeTrue();
        vm.IsPushTestVisible.Should().BeTrue();
    }

    [Fact]
    public void Windows_NeverShowsTheTestButton()
    {
        var vm = Create(Windows, Snapshot(PushRelayAvailability.Enabled, optedIn: true) with { DeviceId = Guid.NewGuid() });

        vm.IsPushTestVisible.Should().BeFalse();
    }

    [Fact]
    public void PlatformWithoutRelay_HidesTheSection()
    {
        var vm = Create(new PlatformCapabilities(), Snapshot(PushRelayAvailability.UnsupportedPlatform, optedIn: false));

        vm.IsPushRelaySectionVisible.Should().BeFalse();
    }

    [Fact]
    public async Task SendPushTest_ShowsTheOutcome()
    {
        _relay.Setup(x => x.SendPushTestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(PushRelayPushTestResult.NoPushToken);
        var vm = Create(Phone, Snapshot(PushRelayAvailability.Enabled, optedIn: false) with { DeviceId = Guid.NewGuid() });

        await vm.SendPushTestCommand.ExecuteAsync(null);

        vm.PushRelayTestResultText.Should().Be(Translator.NotificationSettings_PushRelay_TestNoToken);
    }

    [Theory]
    [InlineData(PushRelayPushTestResult.RateLimited, true)]
    [InlineData(PushRelayPushTestResult.Queued, true)]
    [InlineData(PushRelayPushTestResult.Failed, false)]
    public async Task SendPushTest_RestsAfterASendOrARateLimit(PushRelayPushTestResult result, bool coolsDown)
    {
        _relay.Setup(x => x.SendPushTestAsync(It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var vm = Create(Phone, Snapshot(PushRelayAvailability.Enabled, optedIn: false) with { DeviceId = Guid.NewGuid() });

        await vm.SendPushTestCommand.ExecuteAsync(null);

        vm.PushRelayTestResultText.Should().Be(result switch
        {
            PushRelayPushTestResult.RateLimited => Translator.NotificationSettings_PushRelay_TestRateLimited,
            PushRelayPushTestResult.Queued => Translator.NotificationSettings_PushRelay_TestQueued,
            _ => Translator.NotificationSettings_PushRelay_TestFailed
        });
        vm.PushRelayTestResultText.Should().NotBe(Translator.NotificationSettings_PushRelay_ServiceUnavailable);
        vm.SendPushTestCommand.CanExecute(null).Should().Be(!coolsDown);
    }

    [Fact]
    public async Task Reset_LeavesTheOptInAlone()
    {
        var vm = Create(Mac, Snapshot(PushRelayAvailability.Enabled, optedIn: true));

        await vm.ResetAsync();

        _relay.Verify(x => x.SetUserOptInAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.IsPushRelayEnabled.Should().BeTrue();
    }

    private NotificationSettingsPageViewModel Create(PlatformCapabilities capabilities, PushRelayRegistrationSnapshot snapshot)
    {
        _relay.SetupGet(x => x.Snapshot).Returns(snapshot);
        _relay.SetupGet(x => x.IsUserOptedIn).Returns(snapshot.IsUserOptedIn);
        var policy = new Mock<INotificationPolicyService>();
        policy.Setup(x => x.GetSnoozeState(It.IsAny<DateTimeOffset>()))
            .Returns(new NotificationSnoozeState(false, NotificationSnoozePreset.OneHour, null));

        return new NotificationSettingsPageViewModel(
            Mock.Of<IPreferencesService>(),
            _accounts.Object,
            Mock.Of<ICalendarService>(),
            policy.Object,
            Mock.Of<IUserPresenceStateProvider>(),
            _relay.Object,
            capabilities);
    }

    private static PushRelayMailboxSnapshot Watched()
        => new(Guid.NewGuid(), Guid.NewGuid(), PushRelayMailboxStatuses.Active, false, null, null);

    private static PushRelayRegistrationSnapshot Snapshot(PushRelayAvailability availability, bool optedIn, params PushRelayMailboxSnapshot[] mailboxes)
        => new(availability, true, optedIn, null, true, null, null, null, null, mailboxes.ToList(), 10, null);
}
