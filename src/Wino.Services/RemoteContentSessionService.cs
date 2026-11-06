using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class RemoteContentSessionService : IRemoteContentSessionService
{
    private readonly ConcurrentDictionary<Guid, RemoteContentMode> _overrides = new();

    public void Set(Guid id, RemoteContentMode mode) => _overrides[id] = mode;

    public bool TryGet(Guid id, out RemoteContentMode mode) => _overrides.TryGetValue(id, out mode);

    public void Clear(Guid id) => _overrides.TryRemove(id, out _);

    public Guid GetFileId(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath.Trim().ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
