using System;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class SyncSnapshotCryptographyTests
{
    // Small Argon2 parameters keep the tests fast; the format carries them in the header.
    private static SyncSnapshotKeyParameters TestParameters(byte keySource = SyncSnapshotFormat.KeySourceAccountPassword, Guid? userId = null)
        => new(keySource, 1024, 1, 1, userId is { } id ? SyncSnapshotCryptography.CreateUserSalt(id) : SyncSnapshotCryptography.CreateSalt());

    [Fact]
    public async Task EncryptThenDecrypt_RoundTripsPlaintext_AndHeaderCarriesParameters()
    {
        var parameters = TestParameters(SyncSnapshotFormat.KeySourcePassphrase);
        var key = new SyncSnapshotKey(parameters, await SyncSnapshotCryptography.DeriveKeyAsync("correct horse", parameters));
        var plaintext = Encoding.UTF8.GetBytes("""{"version":1,"preferencesJson":"{}"}""");

        var payload = SyncSnapshotCryptography.Encrypt(plaintext, key);

        SyncSnapshotCryptography.IsSnapshot(payload).Should().BeTrue();
        payload.Length.Should().BeGreaterThan(SyncSnapshotFormat.MinPayloadLength);

        var header = SyncSnapshotCryptography.ReadHeader(payload);
        header.FormatVersion.Should().Be(SyncSnapshotFormat.CurrentFormatVersion);
        header.KeySource.Should().Be(SyncSnapshotFormat.KeySourcePassphrase);
        header.MemoryKiB.Should().Be(1024);
        header.Iterations.Should().Be(1);
        header.Parallelism.Should().Be(1);
        header.IsCompressed.Should().BeTrue();
        header.Salt.Should().Equal(parameters.Salt);

        var otherDeviceKey = await SyncSnapshotCryptography.DeriveKeyAsync("correct horse", header.ToKeyParameters());
        SyncSnapshotCryptography.Decrypt(payload, otherDeviceKey).Should().Equal(plaintext);
    }

    [Fact]
    public async Task Decrypt_WithWrongSecret_ThrowsDecryptionException()
    {
        var parameters = TestParameters();
        var key = new SyncSnapshotKey(parameters, await SyncSnapshotCryptography.DeriveKeyAsync("password-1", parameters));
        var payload = SyncSnapshotCryptography.Encrypt("secret"u8, key);

        var wrongKey = await SyncSnapshotCryptography.DeriveKeyAsync("password-2", parameters);
        var act = () => SyncSnapshotCryptography.Decrypt(payload, wrongKey);

        act.Should().Throw<SyncSnapshotDecryptionException>();
    }

    [Fact]
    public async Task Decrypt_WithTamperedHeader_ThrowsDecryptionException()
    {
        var parameters = TestParameters();
        var key = new SyncSnapshotKey(parameters, await SyncSnapshotCryptography.DeriveKeyAsync("password", parameters));
        var payload = SyncSnapshotCryptography.Encrypt("secret"u8, key);

        // The header is authenticated data, so flipping the key source must fail the tag.
        payload[SyncSnapshotFormat.KeySourceOffset] = SyncSnapshotFormat.KeySourcePassphrase;
        var act = () => SyncSnapshotCryptography.Decrypt(payload, key.Key);

        act.Should().Throw<SyncSnapshotDecryptionException>();
    }

    [Fact]
    public void ReadHeader_RejectsForeignBytesAndFutureVersions()
    {
        var notASnapshot = () => SyncSnapshotCryptography.ReadHeader(Encoding.UTF8.GetBytes("{\"version\":2}"));
        notASnapshot.Should().Throw<SyncSnapshotInvalidFileException>();

        var future = new byte[SyncSnapshotFormat.MinPayloadLength];
        SyncSnapshotFormat.Magic.CopyTo(future);
        future[SyncSnapshotFormat.FormatVersionOffset] = SyncSnapshotFormat.CurrentFormatVersion + 1;
        var tooNew = () => SyncSnapshotCryptography.ReadHeader(future);
        tooNew.Should().Throw<SyncSnapshotInvalidFileException>();
    }

    [Fact]
    public void CreateUserSalt_IsStablePerUser_AndDiffersBetweenUsers()
    {
        var user = Guid.NewGuid();

        SyncSnapshotCryptography.CreateUserSalt(user).Should().Equal(SyncSnapshotCryptography.CreateUserSalt(user));
        SyncSnapshotCryptography.CreateUserSalt(user).Should().NotEqual(SyncSnapshotCryptography.CreateUserSalt(Guid.NewGuid()));
        SyncSnapshotCryptography.CreateUserSalt(user).Should().HaveCount(SyncSnapshotFormat.SaltLength);
    }

    [Fact]
    public async Task DeriveKey_NormalizesSecretToNfc()
    {
        var parameters = TestParameters();
        var composed = await SyncSnapshotCryptography.DeriveKeyAsync("café", parameters);
        var decomposed = await SyncSnapshotCryptography.DeriveKeyAsync("café", parameters);

        composed.Should().Equal(decomposed);
    }
}
