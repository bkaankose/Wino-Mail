using Wino.Authentication;

namespace Wino.Core.Tests.Authentication;

/// <summary>Test-only persistence; portable tests do not load Windows adapters.</summary>
internal sealed class TestGoogleTokenStore(string? root = null) : IGoogleTokenStore
{
    private readonly Dictionary<string, byte[]> _tokens = [];

    public async Task<byte[]> ReadAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (root is not null)
        {
            var path = Path.Combine(root, $"{credentialKey}.json");
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null!;
        }

        return _tokens.TryGetValue(credentialKey, out var token) ? token.ToArray() : null!;
    }

    public Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens[credentialKey] = serializedToken.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.Remove(credentialKey);
        return Task.CompletedTask;
    }
}
