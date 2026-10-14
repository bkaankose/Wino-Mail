using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Services.PushRelay;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class PushRelayRegistrationServiceTests
{
    private readonly FakeRelayClient _relay = new();
    private readonly FakeTokenProvider _tokens = new();
    private readonly InMemoryStateStore _state = new();
    private readonly InMemoryIdentityStore _identity;
    private readonly FakePushTokenProvider _push = new();
    private readonly SettableTimeProvider _clock = new();
    private readonly FakeBuilder _builder = new();
    private readonly List<MailAccount> _accounts = [];
    private readonly Mock<IAppMetadataService> _metadata = new();
    private FakeDeviceInfo _device = new();
    private PlatformCapabilities _capabilities = new(RemoteNotifications: true);
    private Guid _userId = Guid.NewGuid();
    private bool _signedIn = true;

    public PushRelayRegistrationServiceTests()
    {
        _identity = new InMemoryIdentityStore(_relay.Calls);
        _metadata.SetupGet(x => x.AppVersion).Returns("1.0");
        _metadata.SetupGet(x => x.SentryDist).Returns("100");
        _push.SetToken("a1b2");
    }

    [Fact]
    public async Task WithoutWinoSession_SendsNothing()
    {
        _signedIn = false;
        AddAccount();
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Should().BeEmpty();
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.SignInRequired);
    }

    [Fact]
    public async Task OptInPlatformWithoutOptIn_IsDisabledByUser()
    {
        _capabilities = new PlatformCapabilities(RemoteNotifications: true, PushRelayOptIn: true);
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Should().BeEmpty();
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.DisabledByUser);
    }

    [Fact]
    public async Task MissingPlatformAdapters_AreUnsupported()
    {
        var service = new PushRelayRegistrationService(_relay, _tokens, _state, Sessions(), Accounts(), [_builder],
            _capabilities, _metadata.Object, timeProvider: _clock);

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        service.Snapshot.Availability.Should().Be(PushRelayAvailability.UnsupportedPlatform);
        _relay.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstPass_SavesIdentityBeforeSyncingAccounts_AndRegistersMailboxes()
    {
        var account = AddAccount();
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Should().Equal("register", "identity.save", "accounts", "create:" + account.Id.ToString("D"));
        var registration = _relay.Registrations.Single();
        registration.Platform.Should().Be(PushRelayPlatforms.Ios);
        registration.Name.Should().Be("Test iPhone");
        registration.OsVersion.Should().Be("iOS 26.1");
        registration.PushToken.Should().Be("a1b2");
        registration.AppVersion.Should().Be("1.0");
        registration.AppBuild.Should().Be("100");
        _relay.AccountSyncs.Single().Should().Equal(account.Id.ToString("D"));
        _identity.Identity!.WinoUserId.Should().Be(_userId);
        var snapshot = service.Snapshot;
        snapshot.Availability.Should().Be(PushRelayAvailability.Enabled);
        snapshot.DeviceId.Should().Be(_identity.Identity.DeviceId);
        snapshot.Mailboxes.Should().ContainSingle(x => x.AccountId == account.Id && x.IsWatched);
        snapshot.MailboxLimit.Should().Be(10);
    }

    [Fact]
    public async Task SecondPassWithinTwentyHours_IsANoOp()
    {
        AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _relay.Calls.Clear();

        _clock.Advance(TimeSpan.FromHours(19));
        await service.SyncNowAsync(PushRelaySyncReason.Foreground);

        _relay.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterTwentyHours_ChecksIn()
    {
        AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _relay.Calls.Clear();

        _clock.Advance(TimeSpan.FromHours(21));
        await service.SyncNowAsync(PushRelaySyncReason.CheckIn);

        _relay.Calls.Should().StartWith("get");
        _relay.Calls.Should().NotContain(x => x.StartsWith("create") || x.StartsWith("put"));
    }

    [Fact]
    public async Task WinoAccountChange_RegistersANewDeviceWithoutAFailingCall()
    {
        AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        var firstDevice = _identity.Identity!.DeviceId;
        _relay.Calls.Clear();

        _userId = Guid.NewGuid();
        await service.SyncNowAsync(PushRelaySyncReason.WinoAccountChanged);

        _relay.Calls.Take(3).Should().Equal("identity.delete", "register", "identity.save");
        _identity.Identity!.DeviceId.Should().NotBe(firstDevice);
        _identity.Identity.WinoUserId.Should().Be(_userId);
    }

    [Fact]
    public async Task PushTokenNameOrOsChange_PatchesTheDevice()
    {
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _relay.Calls.Clear();

        _push.SetToken("ffff");
        await service.SyncNowAsync(PushRelaySyncReason.PushTokenChanged);
        _device.Name = "Renamed";
        await service.SyncNowAsync(PushRelaySyncReason.Foreground);
        _device.OsVersion = "iOS 27";
        await service.SyncNowAsync(PushRelaySyncReason.Foreground);

        _relay.Calls.Count(x => x == "patch").Should().Be(3);
        _relay.Updates[0].PushToken.Should().Be("ffff");
        _relay.Updates[1].Name.Should().Be("Renamed");
        _relay.Updates[2].OsVersion.Should().Be("iOS 27");
        _relay.Calls.Should().NotContain("register");
    }

    [Fact]
    public async Task CredentialChange_PutsCredentials()
    {
        var account = AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        var subscription = _relay.Subscriptions[account.Id.ToString("D")];
        _relay.Calls.Clear();

        _builder.Fingerprints[account.Id] = "changed";
        await service.SyncNowAsync(PushRelaySyncReason.CredentialsChanged);

        _relay.Calls.Should().Equal("put:" + subscription);
    }

    [Fact]
    public async Task AlreadyAddedMailbox_PutsCredentialsOnTheReturnedSubscription()
    {
        var account = AddAccount();
        var existing = Guid.NewGuid();
        _relay.OnCreate = _ => throw FakeRelayClient.Error(409, PushRelayErrorCodes.MailboxAlreadyAdded, subscriptionId: existing);
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Should().EndWith(new[] { "create:" + account.Id.ToString("D"), "put:" + existing });
        service.Snapshot.Mailboxes.Single().SubscriptionId.Should().Be(existing);
    }

    [Fact]
    public async Task MailboxLimit_IsRecordedAndLaterAccountsAreNotAttempted()
    {
        var first = AddAccount();
        var second = AddAccount();
        _relay.OnCreate = _ => throw FakeRelayClient.Error(422, PushRelayErrorCodes.MailboxLimitReached);
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Should().Contain("create:" + first.Id.ToString("D"));
        _relay.Calls.Should().NotContain("create:" + second.Id.ToString("D"));
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.Enabled);
        service.Snapshot.Mailboxes.Should().HaveCount(2).And.OnlyContain(x => x.LastErrorCode == PushRelayErrorCodes.MailboxLimitReached && !x.IsWatched);
    }

    [Fact]
    public async Task DeviceRevoked_RegistersAgainOnce()
    {
        AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        var revokedDevice = _identity.Identity!.DeviceId;
        _relay.DeviceCallFailure = id => id == revokedDevice ? FakeRelayClient.Error(401, "unauthorized", "device_revoked") : null;
        _relay.Calls.Clear();

        _clock.Advance(TimeSpan.FromHours(21));
        await service.SyncNowAsync(PushRelaySyncReason.CheckIn);

        _relay.Calls.Take(4).Should().Equal("get", "identity.delete", "register", "identity.save");
        _identity.Identity!.DeviceId.Should().NotBe(revokedDevice);
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.Enabled);
        service.Snapshot.Mailboxes.Single().IsWatched.Should().BeTrue();
    }

    [Fact]
    public async Task DeviceRevokedTwice_BacksOff()
    {
        AddAccount();
        _relay.DeviceCallFailure = _ => FakeRelayClient.Error(401, "unauthorized", "device_revoked");
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _relay.Calls.Count(x => x == "register").Should().Be(2);
        var state = _state.Load();
        state.FailureCount.Should().Be(1);
        state.NextAttemptUtc.Should().Be(_clock.Now.UtcDateTime.AddMinutes(1));
    }

    [Fact]
    public async Task RateLimit_WaitsForRetryAfter()
    {
        _relay.OnRegister = _ => throw FakeRelayClient.Error(429, PushRelayErrorCodes.RateLimited, retryAfter: 90);
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);

        _state.Load().NextAttemptUtc.Should().Be(_clock.Now.UtcDateTime.AddSeconds(90));
        _state.Load().FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task TransientFailures_BackOffAndHonourTheSchedule()
    {
        _relay.OnRegister = _ => throw FakeRelayClient.Error(503, PushRelayErrorCodes.ServiceUnavailable);
        var service = CreateService();

        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _state.Load().NextAttemptUtc.Should().Be(_clock.Now.UtcDateTime.AddMinutes(1));
        _relay.Calls.Clear();

        await service.SyncNowAsync(PushRelaySyncReason.Foreground);
        _relay.Calls.Should().BeEmpty("the backoff has not elapsed");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await service.SyncNowAsync(PushRelaySyncReason.Foreground);
        await service.SyncNowAsync(PushRelaySyncReason.UserRequested);

        var state = _state.Load();
        state.FailureCount.Should().Be(3);
        state.NextAttemptUtc.Should().Be(_clock.Now.UtcDateTime.AddMinutes(15));
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.ServiceUnavailable);
    }

    [Fact]
    public async Task WindowsRejected_StaysRejectedUntilTheAppVersionChanges()
    {
        _device = new FakeDeviceInfo(PushRelayPlatforms.Windows);
        _capabilities = new PlatformCapabilities(PushRelayOptIn: true);
        _relay.OnRegister = _ => throw FakeRelayClient.Error(400, PushRelayErrorCodes.InvalidRequest);
        var service = CreateService();
        await service.SetUserOptInAsync(true);

        service.Snapshot.Availability.Should().Be(PushRelayAvailability.RejectedByRelay);
        _relay.Calls.Clear();

        await service.SyncNowAsync(PushRelaySyncReason.UserRequested);
        _relay.Calls.Should().BeEmpty();

        _metadata.SetupGet(x => x.AppVersion).Returns("1.1");
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _relay.Calls.Should().Equal("register");
    }

    [Fact]
    public async Task RequestsDuringAPass_AreCoalescedIntoOneMorePass()
    {
        _relay.BlockRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService();
        var published = 0;
        service.SnapshotChanged += (_, _) => Interlocked.Increment(ref published);

        await service.StartAsync();
        await WaitUntilAsync(() => _relay.Calls.Contains("register"));
        service.RequestSync(PushRelaySyncReason.AccountsChanged);
        service.RequestSync(PushRelaySyncReason.Foreground);
        service.RequestSync(PushRelaySyncReason.CheckIn);
        _relay.BlockRegister.SetResult();
        await service.WaitForIdleAsync();

        published.Should().Be(2);
        _relay.Calls.Count(x => x == "register").Should().Be(1);
    }

    [Fact]
    public async Task RequestsBeforeStart_WaitForStart()
    {
        AddAccount();
        var service = CreateService();

        service.RequestSync(PushRelaySyncReason.Foreground);
        await Task.Delay(100);
        _relay.Calls.Should().BeEmpty();

        await service.StartAsync();
        await WaitUntilAsync(() => _relay.Calls.Contains("register"));
        await service.WaitForIdleAsync();

        _relay.Calls.Count(x => x == "register").Should().Be(1);
    }

    [Fact]
    public async Task OptingOut_DeletesTheDeviceAndKeepsTheChoice()
    {
        _capabilities = new PlatformCapabilities(RemoteNotifications: true, PushRelayOptIn: true);
        AddAccount();
        var service = CreateService();
        await service.SetUserOptInAsync(true);
        _identity.Identity.Should().NotBeNull();
        _relay.Calls.Clear();

        await service.SetUserOptInAsync(false);

        _relay.Calls.Should().Equal("delete", "identity.delete");
        _identity.Identity.Should().BeNull();
        var state = _state.Load();
        state.UserOptIn.Should().BeFalse();
        state.Mailboxes.Should().BeEmpty();
        service.Snapshot.Availability.Should().Be(PushRelayAvailability.DisabledByUser);
        service.IsUserOptedIn.Should().BeFalse();
    }

    [Fact]
    public async Task SignOut_DeletesTheDeviceAndClearsStateEvenWhenTheRelayFails()
    {
        AddAccount();
        var service = CreateService();
        await service.SyncNowAsync(PushRelaySyncReason.Startup);
        _relay.OnDelete = () => FakeRelayClient.Error(503, PushRelayErrorCodes.ServiceUnavailable);
        _relay.Calls.Clear();

        await service.SignOutAsync();

        _relay.Calls.Should().Equal("delete", "identity.delete");
        _identity.Identity.Should().BeNull();
        _state.Load().Mailboxes.Should().BeEmpty();
        _state.Load().LastCheckInUtc.Should().BeNull();
    }

    [Fact]
    public async Task ReauthReport_ForgetsTheFingerprintAndUploadsAgain()
    {
        var account = AddAccount();
        var service = CreateService();
        await service.StartAsync();
        await WaitUntilAsync(() => _relay.Subscriptions.ContainsKey(account.Id.ToString("D")));
        await service.WaitForIdleAsync();
        var subscription = _relay.Subscriptions[account.Id.ToString("D")];
        _relay.Calls.Clear();

        service.ReportMailboxStatus(account.Id, PushRelayMailboxStatuses.NeedsReauth, "auth", "Login failed");
        await service.WaitForIdleAsync();

        _relay.Calls.Should().Equal("put:" + subscription);
    }

    private MailAccount AddAccount()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), ProviderType = MailProviderType.IMAP4, IsMailAccessGranted = true, Address = "a@example.test" };
        _accounts.Add(account);
        return account;
    }

    private PushRelayRegistrationService CreateService()
        => new(_relay, _tokens, _state, Sessions(), Accounts(), [_builder], _capabilities, _metadata.Object,
            identityStore: _identity, deviceInfo: new DelegatingDeviceInfo(() => _device), pushTokens: _push, timeProvider: _clock);

    private IWinoAccountSessionService Sessions()
    {
        var sessions = new Mock<IWinoAccountSessionService>();
        sessions.Setup(x => x.CaptureAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _signedIn ? new WinoAccountSession(_userId, 1, CancellationToken.None) : null);
        return sessions.Object;
    }

    private IAccountService Accounts()
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(() => _accounts.ToList());
        return accounts.Object;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    private sealed class DelegatingDeviceInfo(Func<IPushRelayDeviceInfo> current) : IPushRelayDeviceInfo
    {
        public PushRelayDeviceDescriptor Describe() => current().Describe();
    }

    private sealed class FakeBuilder : IPushRelayMailboxRegistrationBuilder
    {
        public Dictionary<Guid, string> Fingerprints { get; } = [];
        public MailProviderType ProviderType => MailProviderType.IMAP4;

        public Task<bool> IsEligibleAsync(MailAccount account, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<PushRelayMailboxRegistrationRequest?> BuildAsync(MailAccount account, CancellationToken cancellationToken = default)
        {
            var id = account.Id.ToString("D");
            var credentials = new PushRelayImapCredentials("imap.example.test", 993, "ssl", "user", "pw", "password");
            var fingerprint = Fingerprints.TryGetValue(account.Id, out var value) ? value : "fp-" + id;
            return Task.FromResult<PushRelayMailboxRegistrationRequest?>(new PushRelayMailboxRegistrationRequest(
                new PushRelayCreateMailboxRequest("imap", id, credentials), new PushRelayUpdateCredentialsRequest(credentials), fingerprint));
        }
    }
}
