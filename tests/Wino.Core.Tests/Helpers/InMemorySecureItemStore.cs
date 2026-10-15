#nullable enable
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Tests.Helpers;

/// <summary>An <see cref="ISecureItemStore"/> in memory, with the Keychain store's copy and missing-item semantics.</summary>
internal sealed class InMemorySecureItemStore : ISecureItemStore
{
    private readonly Dictionary<(string Scope, string Account), byte[]> _items = new();

    public IReadOnlyCollection<(string Scope, string Account)> Keys => _items.Keys;
    public Dictionary<(string Scope, string Account), SecureItemAccess> Access { get; } = new();

    public byte[]? Read(string scope, string account)
        => _items.TryGetValue((scope, account), out var bytes) ? (byte[])bytes.Clone() : null;

    public void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default)
    {
        _items[(scope, account)] = (byte[])bytes.Clone();
        Access[(scope, account)] = access;
    }

    public bool TryAdd(string scope, string account, byte[] bytes)
        => _items.TryAdd((scope, account), (byte[])bytes.Clone());

    public void Delete(string scope, string account) => _items.Remove((scope, account));

    public void DeleteScope(string scope)
    {
        foreach (var key in _items.Keys.Where(key => key.Scope == scope).ToArray())
            _items.Remove(key);
    }
}
