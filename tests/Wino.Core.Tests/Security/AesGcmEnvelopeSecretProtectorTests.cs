#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services.Security;
using Xunit;

namespace Wino.Core.Tests.Security;

public sealed class AesGcmEnvelopeSecretProtectorTests
{
    private const string Identity = "com.winomail.macos";
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("Wino.Test.Context");

    // "secret" protected with key 00..1F, nonce A0..AB, identity com.winomail.macos and Context. Produced by an
    // independent AesGcm implementation of the v1 layout; the Mac and iOS apps store envelopes in this format.
    private const string FixedEnvelope = "574D4B500106000000A0A1A2A3A4A5A6A7A8A9AAAB4573265E91D4A506FF9B0069FCC25F31957D1F5F20BF";

    [Fact]
    public void ProtectThenUnprotect_ReturnsPlaintextInVersionOneLayout()
    {
        var protector = CreateProtector();
        var plaintext = Encoding.UTF8.GetBytes("päss 密碼");

        var envelope = protector.Protect(plaintext, Context);

        envelope.Length.Should().Be(9 + 12 + 16 + plaintext.Length);
        envelope.AsSpan(0, 4).ToArray().Should().Equal("WMKP"u8.ToArray());
        envelope[4].Should().Be(1);
        BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(5, 4)).Should().Be(plaintext.Length);
        protector.Unprotect(envelope, Context).Should().Equal(plaintext);
        protector.Protect(plaintext, Context).Should().NotEqual(envelope, "every envelope has a fresh nonce");
    }

    [Fact]
    public void FixedEnvelope_StaysReadable()
    {
        CreateProtector().Unprotect(Convert.FromHexString(FixedEnvelope), Context)
            .Should().Equal(Encoding.UTF8.GetBytes("secret"));
    }

    [Fact]
    public void Protect_BindsHeaderIdentityAndContextAsAssociatedData()
    {
        var plaintext = Encoding.UTF8.GetBytes("layout");
        var envelope = CreateProtector().Protect(plaintext, Context);
        var identity = Encoding.UTF8.GetBytes(Identity);
        var aad = new List<byte>(envelope.AsSpan(0, 9).ToArray());
        aad.AddRange(Int32LittleEndian(identity.Length));
        aad.AddRange(identity);
        aad.AddRange(Int32LittleEndian(Context.Length));
        aad.AddRange(Context);
        var decrypted = new byte[plaintext.Length];

        using var aes = new AesGcm(TestKey(), 16);
        aes.Decrypt(envelope.AsSpan(9, 12), envelope.AsSpan(37), envelope.AsSpan(21, 16), decrypted, aad.ToArray());

        decrypted.Should().Equal(plaintext);
    }

    [Theory]
    [InlineData(9)]   // nonce
    [InlineData(21)]  // tag
    [InlineData(37)]  // ciphertext
    public void TamperedEnvelope_ThrowsCryptographicException(int offset)
    {
        var protector = CreateProtector();
        var envelope = protector.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        envelope[offset] ^= 0x01;

        var act = () => protector.Unprotect(envelope, Context);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void WrongContextOrIdentity_ThrowsCryptographicException()
    {
        var store = new InMemorySecureItemStore();
        var protector = CreateProtector(store);
        var envelope = protector.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        var otherIdentity = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(store, "com.example.other"));

        ((Action)(() => protector.Unprotect(envelope, Encoding.UTF8.GetBytes("Wino.Other")))).Should().Throw<CryptographicException>();
        ((Action)(() => otherIdentity.Unprotect(envelope, Context))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void InvalidHeaderOrLength_ThrowsCryptographicException()
    {
        var protector = CreateProtector();
        var valid = protector.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        var wrongMagic = (byte[])valid.Clone(); wrongMagic[0] = (byte)'X';
        var wrongVersion = (byte[])valid.Clone(); wrongVersion[4] = 2;
        var wrongLength = (byte[])valid.Clone(); BinaryPrimitives.WriteInt32LittleEndian(wrongLength.AsSpan(5, 4), 7);
        var negativeLength = (byte[])valid.Clone(); BinaryPrimitives.WriteInt32LittleEndian(negativeLength.AsSpan(5, 4), -1);

        foreach (var envelope in new[] { valid[..36], wrongMagic, wrongVersion, wrongLength, negativeLength, valid[..^1] })
            ((Action)(() => protector.Unprotect(envelope, Context))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void MissingKey_ThrowsCredentialMissing_AndInvalidKeyThrowsCryptographicException()
    {
        var store = new InMemorySecureItemStore();
        var protector = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(store, Identity));

        ((Action)(() => protector.Protect([1], Context))).Should().Throw<AccountCredentialMissingException>();

        store.Write(SecureItemSecretProtectionKeyProvider.KeyScope, SecureItemSecretProtectionKeyProvider.KeyAccount, new byte[16]);
        ((Action)(() => protector.Protect([1], Context))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Provisioning_RequiresVerifiedNewInstallation_AndKeepsAnExistingKey()
    {
        var store = new InMemorySecureItemStore();
        var protector = new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(store, Identity));

        ((Action)(() => protector.ProvisionNewInstallationKey(false))).Should().Throw<InvalidOperationException>();
        store.Keys.Should().BeEmpty();

        protector.ProvisionNewInstallationKey(true);
        var key = store.Read(SecureItemSecretProtectionKeyProvider.KeyScope, SecureItemSecretProtectionKeyProvider.KeyAccount);
        key.Should().NotBeNull().And.HaveCount(32);
        var envelope = protector.Protect(Encoding.UTF8.GetBytes("secret"), Context);

        protector.ProvisionNewInstallationKey(true);
        store.Read(SecureItemSecretProtectionKeyProvider.KeyScope, SecureItemSecretProtectionKeyProvider.KeyAccount).Should().Equal(key);
        protector.Unprotect(envelope, Context).Should().Equal(Encoding.UTF8.GetBytes("secret"));
    }

    private static byte[] Int32LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] TestKey() => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    private static AesGcmEnvelopeSecretProtector CreateProtector(InMemorySecureItemStore? store = null)
    {
        store ??= new InMemorySecureItemStore();
        store.Write(SecureItemSecretProtectionKeyProvider.KeyScope, SecureItemSecretProtectionKeyProvider.KeyAccount, TestKey());
        return new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(store, Identity));
    }
}
