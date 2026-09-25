using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// How the device result key follows the Wino Intelligence add-on.
/// </summary>
public sealed class IntelligenceResultKeyLifecycleTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("5a5b5c5d-6e6f-7071-7273-747576777879");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wino-lifecycle-{Guid.NewGuid():N}");
    private readonly IntelligenceResultKeyPresence _presence;
    private readonly Mock<IMailIntelligenceCoordinator> _coordinator = new(MockBehavior.Strict);
    private readonly Mock<IWinoAccountIntelligenceSnapshotService> _entitlement = new();
    private readonly StrongReferenceMessenger _messenger = new();

    public IntelligenceResultKeyLifecycleTests()
    {
        Directory.CreateDirectory(_folder);
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(_folder);
        _presence = new IntelligenceResultKeyPresence(configuration.Object);
        _coordinator.Setup(x => x.ResumeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _coordinator.Setup(x => x.AbandonJobsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription)]
    [InlineData(WinoIntelligenceEntitlementState.Expired)]
    [InlineData(WinoIntelligenceEntitlementState.SignedOut)]
    [InlineData(WinoIntelligenceEntitlementState.Unavailable)]
    public async Task UserWithoutTheAddOn_NeverTouchesTheKeyStoreOrJobs(WinoIntelligenceEntitlementState state)
    {
        var keys = new ThrowingKeyStore();
        var coordinator = new Mock<IMailIntelligenceCoordinator>(MockBehavior.Strict);
        using var lifecycle = new IntelligenceResultKeyLifecycle(keys, coordinator.Object, _entitlement.Object, _presence, _messenger);

        await lifecycle.ApplyAsync(Snapshot(state, authoritative: true));
        await lifecycle.ApplyAsync(Snapshot(state, authoritative: false));

        keys.Calls.Should().Be(0);
        coordinator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Active_CreatesAKeyAndResumesJobs()
    {
        var keys = new RecordingKeyStore(_presence);
        using var lifecycle = Create(keys);

        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: false));

        keys.Keys.Should().ContainSingle();
        _presence.MayExist.Should().BeTrue();
        _coordinator.Verify(x => x.ResumeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(WinoIntelligenceEntitlementState.QuotaExhausted, true)]
    [InlineData(WinoIntelligenceEntitlementState.Unavailable, false)]
    [InlineData(WinoIntelligenceEntitlementState.Expired, false)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription, false)]
    public async Task TransientOrUnconfirmedStates_KeepTheKeyAndJobs(WinoIntelligenceEntitlementState state, bool authoritative)
    {
        var keys = new RecordingKeyStore(_presence);
        using var lifecycle = Create(keys);
        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: true));

        // Held for as long as the state lasts: repeated snapshots change nothing.
        for (var day = 0; day < 60; day++)
        {
            await lifecycle.ApplyAsync(Snapshot(state, authoritative));
        }

        keys.Keys.Should().ContainSingle();
        _coordinator.Verify(x => x.AbandonJobsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(WinoIntelligenceEntitlementState.Expired)]
    [InlineData(WinoIntelligenceEntitlementState.NoSubscription)]
    [InlineData(WinoIntelligenceEntitlementState.SignedOut)]
    public async Task AuthoritativeEnd_AbandonsJobsAndDeletesTheKey(WinoIntelligenceEntitlementState state)
    {
        var keys = new RecordingKeyStore(_presence);
        using var lifecycle = Create(keys);
        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: true));

        await lifecycle.ApplyAsync(Snapshot(state, authoritative: true));

        keys.Keys.Should().BeEmpty();
        _presence.MayExist.Should().BeFalse();
        _coordinator.Verify(x => x.AbandonJobsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenewalAfterExpiry_CreatesADifferentKey()
    {
        var keys = new RecordingKeyStore(_presence);
        using var lifecycle = Create(keys);
        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: true));
        var before = keys.Keys[0];

        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Expired, authoritative: true));
        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: true));

        keys.Keys.Should().ContainSingle().Which.KeyId.Should().NotBe(before.KeyId);
    }

    [Fact]
    public async Task EntitlementMessages_DriveTheLifecycle()
    {
        var keys = new RecordingKeyStore(_presence);
        using var lifecycle = Create(keys);

        _messenger.Send(new WinoIntelligenceEntitlementChanged(Snapshot(WinoIntelligenceEntitlementState.Active, authoritative: true)));
        await lifecycle.ApplyAsync(Snapshot(WinoIntelligenceEntitlementState.Unavailable, authoritative: false));

        keys.Keys.Should().ContainSingle();
    }

    private IntelligenceResultKeyLifecycle Create(IIntelligenceResultKeyStore keys)
        => new(keys, _coordinator.Object, _entitlement.Object, _presence, _messenger);

    private static WinoIntelligenceEntitlementSnapshot Snapshot(WinoIntelligenceEntitlementState state, bool authoritative)
        => state == WinoIntelligenceEntitlementState.SignedOut
            ? WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow)
            : new(state, UserId, DateTimeOffset.UtcNow) { IsAuthoritative = authoritative };

    private sealed class ThrowingKeyStore : IIntelligenceResultKeyStore
    {
        public int Calls { get; private set; }

        private T Fail<T>()
        {
            Calls++;
            throw new InvalidOperationException("The key store must not be touched.");
        }

        public Task<IntelligenceResultKey?> GetActiveKeyAsync(Guid winoUserId, CancellationToken cancellationToken = default) => Fail<Task<IntelligenceResultKey?>>();
        public Task<IReadOnlyList<IntelligenceResultKey>> GetKeysAsync(CancellationToken cancellationToken = default) => Fail<Task<IReadOnlyList<IntelligenceResultKey>>>();
        public Task<IntelligenceResultKey> GetOrCreateAsync(Guid winoUserId, CancellationToken cancellationToken = default) => Fail<Task<IntelligenceResultKey>>();
        public Task<byte[]> DecryptAsync(string keyId, ReadOnlyMemory<byte> encodedEnvelope, Guid mailboxId, string route, CancellationToken cancellationToken = default) => Fail<Task<byte[]>>();
        public Task DeleteAsync(string keyId, CancellationToken cancellationToken = default) => Fail<Task>();
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => Fail<Task>();
    }

    private sealed class RecordingKeyStore(IntelligenceResultKeyPresence presence) : IIntelligenceResultKeyStore
    {
        public List<IntelligenceResultKey> Keys { get; } = [];

        public Task<IntelligenceResultKey?> GetActiveKeyAsync(Guid winoUserId, CancellationToken cancellationToken = default)
            => Task.FromResult(Keys.Find(key => key.WinoUserId == winoUserId));

        public Task<IReadOnlyList<IntelligenceResultKey>> GetKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IntelligenceResultKey>>([.. Keys]);

        public Task<IntelligenceResultKey> GetOrCreateAsync(Guid winoUserId, CancellationToken cancellationToken = default)
        {
            var existing = Keys.Find(key => key.WinoUserId == winoUserId);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            presence.MarkPresent();
            var created = new IntelligenceResultKey($"dev-202609-{Guid.NewGuid():N}"[..19], winoUserId, "pem", DateTime.UtcNow);
            Keys.Add(created);
            return Task.FromResult(created);
        }

        public Task<byte[]> DecryptAsync(string keyId, ReadOnlyMemory<byte> encodedEnvelope, Guid mailboxId, string route, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string keyId, CancellationToken cancellationToken = default)
        {
            Keys.RemoveAll(key => key.KeyId == keyId);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            Keys.Clear();
            presence.MarkAbsent();
            return Task.CompletedTask;
        }
    }
}
