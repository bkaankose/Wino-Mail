using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// The device result key store over the real intelligence database, with a fake DPAPI so the
/// tests run anywhere.
/// </summary>
public sealed class IntelligenceResultKeyStoreTests : IAsyncLifetime
{
    private static readonly Guid UserId = Guid.Parse("a1a2a3a4-b1b2-c1c2-d1d2-e1e2e3e4e5e6");
    private static readonly Guid MailboxId = Guid.Parse("f1f2f3f4-0102-0304-0506-070809101112");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wino-keys-{Guid.NewGuid():N}");
    private MailIntelligenceStore _database = null!;
    private IntelligenceResultKeyPresence _presence = null!;
    private IntelligenceResultKeyStore _store = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(_folder);
        _database = new MailIntelligenceStore(configuration.Object);
        _presence = new IntelligenceResultKeyPresence(configuration.Object);
        _store = new IntelligenceResultKeyStore(_database, new FakeProtector(), _presence);
        return _database.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task GetOrCreate_MakesAnRsa3072KeyNamedByItsFingerprint()
    {
        var key = await _store.GetOrCreateAsync(UserId);

        key.KeyId.Should().MatchRegex("^dev-[0-9]{6}-[0-9a-f]{8}$");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PublicKeyPem);
        rsa.KeySize.Should().Be(3072);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        key.KeyId.Should().EndWith(fingerprint[..8]);
        _presence.MayExist.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreate_ReusesTheExistingKey()
    {
        var first = await _store.GetOrCreateAsync(UserId);
        var second = await _store.GetOrCreateAsync(UserId);

        second.KeyId.Should().Be(first.KeyId);
        (await _store.GetKeysAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ParallelCreates_ProduceExactlyOneKey()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _store.GetOrCreateAsync(UserId)));

        (await _store.GetKeysAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task PrivateKey_IsStoredOnlyInProtectedForm()
    {
        var key = await _store.GetOrCreateAsync(UserId);

        var rows = (IIntelligenceResultKeyRows)_database;
        var row = await rows.GetAsync(key.KeyId, default);
        row!.PrivateKeyProtected.Should().NotBeEmpty();
        FakeProtector.IsProtected(row.PrivateKeyProtected).Should().BeTrue();
    }

    [Fact]
    public async Task Decrypt_OpensAPageEncryptedToTheDeviceKey()
    {
        var key = await _store.GetOrCreateAsync(UserId);
        const string route = "/api/v2/ai/intelligence/mailboxes/x/jobs/y/results/classification/page-0";
        var encoded = EncryptTo(key, UserId, route, "{\"items\":[]}");

        var plaintext = await _store.DecryptAsync(key.KeyId, encoded, MailboxId, route);

        Encoding.UTF8.GetString(plaintext).Should().Be("{\"items\":[]}");
    }

    [Fact]
    public async Task Decrypt_RefusesAPageBoundToAnotherPage()
    {
        var key = await _store.GetOrCreateAsync(UserId);
        var encoded = EncryptTo(key, UserId, "/results/classification/page-0", "{}");

        var act = () => _store.DecryptAsync(key.KeyId, encoded, MailboxId, "/results/classification/page-1");

        await act.Should().ThrowAsync<IntelligenceResultKeyLostException>();
    }

    [Fact]
    public async Task Decrypt_WithADeletedKey_ReportsTheKeyLost()
    {
        var key = await _store.GetOrCreateAsync(UserId);
        var encoded = EncryptTo(key, UserId, "/r", "{}");
        await _store.DeleteAsync(key.KeyId);

        var act = () => _store.DecryptAsync(key.KeyId, encoded, MailboxId, "/r");

        (await act.Should().ThrowAsync<IntelligenceResultKeyLostException>()).Which.KeyId.Should().Be(key.KeyId);
    }

    [Fact]
    public async Task Decrypt_WhenDpapiCannotUnwrap_ReportsTheKeyLost()
    {
        var key = await _store.GetOrCreateAsync(UserId);
        var encoded = EncryptTo(key, UserId, "/r", "{}");
        var otherProfile = new IntelligenceResultKeyStore(_database, new FakeProtector(failUnprotect: true), _presence);

        var act = () => otherProfile.DecryptAsync(key.KeyId, encoded, MailboxId, "/r");

        await act.Should().ThrowAsync<IntelligenceResultKeyLostException>();
    }

    [Fact]
    public async Task DeleteAll_RemovesEveryKeyAndClearsThePresenceMarker()
    {
        await _store.GetOrCreateAsync(UserId);
        await _store.DeleteAllAsync();

        (await _store.GetKeysAsync()).Should().BeEmpty();
        _presence.MayExist.Should().BeFalse();
    }

    private static byte[] EncryptTo(IntelligenceResultKey key, Guid userId, string route, string json)
    {
        var envelope = new PemContentEnvelopeEncryptor(new ContentEncryptionPublicKey(key.KeyId, key.PublicKeyPem))
            .Encrypt(Encoding.UTF8.GetBytes(json), new ContentEnvelopeContext(userId, MailboxId, route), Guid.NewGuid(), DateTimeOffset.UtcNow);
        return ContentEnvelopeBinaryCodec.Encode(envelope);
    }

    /// <summary>Reversible, entropy-bound stand-in for DPAPI.</summary>
    private sealed class FakeProtector(bool failUnprotect = false) : IIntelligenceKeyProtector
    {
        private static readonly byte[] Prefix = "FAKE-DPAPI:"u8.ToArray();

        public byte[] Protect(byte[] data, byte[] entropy)
            => [.. Prefix, .. Xor(data, entropy)];

        public byte[] Unprotect(byte[] data, byte[] entropy)
            => failUnprotect || !IsProtected(data)
                ? throw new CryptographicException("The data could not be unprotected.")
                : Xor(data[Prefix.Length..], entropy);

        public static bool IsProtected(byte[] data) => data.AsSpan().StartsWith(Prefix);

        private static byte[] Xor(byte[] data, byte[] entropy)
            => [.. data.Select((value, index) => (byte)(value ^ entropy[index % entropy.Length]))];
    }
}
