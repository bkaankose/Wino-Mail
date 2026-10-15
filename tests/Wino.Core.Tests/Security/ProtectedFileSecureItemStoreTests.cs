#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services.Security;
using Xunit;

namespace Wino.Core.Tests.Security;

public sealed class ProtectedFileSecureItemStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-secure-items-" + Guid.NewGuid().ToString("N"));
    private readonly ProtectedFileSecureItemStore _store;

    public ProtectedFileSecureItemStoreTests()
    {
        var keys = new InMemorySecureItemStore();
        keys.Write(SecureItemSecretProtectionKeyProvider.KeyScope, SecureItemSecretProtectionKeyProvider.KeyAccount,
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        _store = new ProtectedFileSecureItemStore(_root,
            new AesGcmEnvelopeSecretProtector(new SecureItemSecretProtectionKeyProvider(keys, "com.winomail.android")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void WriteThenRead_ReturnsBytesFromAnEncryptedFileWithHashedNames()
    {
        var secret = Encoding.UTF8.GetBytes("hunter2 secret");

        _store.Write("mail-passwords.abc", "revision-1", secret);

        _store.Read("mail-passwords.abc", "revision-1").Should().Equal(secret);
        var path = _store.ItemPath("mail-passwords.abc", "revision-1");
        path.Should().Be(Path.Combine(Path.GetFullPath(_root), ProtectedFileSecureItemStore.HashName("mail-passwords.abc"),
            ProtectedFileSecureItemStore.HashName("revision-1") + ".item"));
        var stored = File.ReadAllBytes(path);
        stored.AsSpan(0, 4).ToArray().Should().Equal("WMKP"u8.ToArray());
        Encoding.UTF8.GetString(stored).Should().NotContain("hunter2");
        Directory.GetFiles(_store.ScopePath("mail-passwords.abc")).Should().ContainSingle("no temporary file is left behind");
    }

    [Fact]
    public void HashName_IsLowercaseHexSha256()
        => ProtectedFileSecureItemStore.HashName("abc").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

    [Fact]
    public void CreateContext_LengthPrefixesScopeAndAccount()
    {
        var context = ProtectedFileSecureItemStore.CreateContext("ab", "c");

        var expected = new List<byte>("wino-secure-item:v1"u8.ToArray());
        expected.AddRange(Int32LittleEndian(2));
        expected.AddRange("ab"u8.ToArray());
        expected.AddRange(Int32LittleEndian(1));
        expected.AddRange("c"u8.ToArray());
        context.Should().Equal(expected);
        ProtectedFileSecureItemStore.CreateContext("a", "bc").Should().NotEqual(context);
    }

    [Fact]
    public void Read_MissingItemOrScope_ReturnsNull()
    {
        _store.Read("scope", "missing").Should().BeNull();
        _store.Write("scope", "present", [1]);
        _store.Read("scope", "missing").Should().BeNull();
    }

    [Fact]
    public void Write_ReplacesTheItem_ForEitherAccess()
    {
        _store.Write("scope", "account", [1, 2]);
        _store.Write("scope", "account", [3], SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);

        _store.Read("scope", "account").Should().Equal(3);
        ((Action)(() => _store.Write("scope", "account", [4], (SecureItemAccess)42))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryAdd_KeepsAnExistingItem()
    {
        _store.TryAdd("scope", "account", [1]).Should().BeTrue();
        _store.TryAdd("scope", "account", [2]).Should().BeFalse();

        _store.Read("scope", "account").Should().Equal(1);
        Directory.GetFiles(_store.ScopePath("scope")).Should().ContainSingle();
    }

    [Fact]
    public void Delete_RemovesOneItemAndIgnoresMissingOnes()
    {
        _store.Delete("scope", "missing");
        _store.Write("scope", "a", [1]);
        _store.Write("scope", "b", [2]);

        _store.Delete("scope", "a");
        _store.Delete("scope", "a");

        _store.Read("scope", "a").Should().BeNull();
        _store.Read("scope", "b").Should().Equal(2);
    }

    [Fact]
    public void DeleteScope_RemovesOnlyThatScope()
    {
        _store.DeleteScope("missing");
        _store.Write("first", "a", [1]);
        _store.Write("first", "b", [2]);
        _store.Write("second", "a", [3]);

        _store.DeleteScope("first");

        _store.Read("first", "a").Should().BeNull();
        _store.Read("first", "b").Should().BeNull();
        _store.Read("second", "a").Should().Equal(3);
    }

    [Fact]
    public void Read_ItemMovedToAnotherName_FailsAsCorrupt()
    {
        _store.Write("scope", "a", [1]);
        Directory.CreateDirectory(_store.ScopePath("other"));
        File.Copy(_store.ItemPath("scope", "a"), _store.ItemPath("scope", "b"));
        File.Copy(_store.ItemPath("scope", "a"), _store.ItemPath("other", "a"));

        ((Action)(() => _store.Read("scope", "b"))).Should().Throw<CryptographicException>();
        ((Action)(() => _store.Read("other", "a"))).Should().Throw<CryptographicException>();
    }

    private static byte[] Int32LittleEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }
}
