#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Windows;

/// <summary>
/// OAuth tokens for IMAP, SMTP and Exchange accounts, one file per account in LocalState with the same protection as
/// <see cref="WindowsGoogleTokenStore"/>. Both move together when Windows credentials move to DPAPI-protected storage.
/// </summary>
public sealed class WindowsOAuthTokenStore : IOAuthTokenStore
{
    private readonly string _root;

    public WindowsOAuthTokenStore(IApplicationConfiguration configuration)
        => _root = AuthenticationTokenStorePaths.GetOAuthTokenStorePath(configuration);

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var bytes = new MemoryStream();
        try
        {
            await stream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.GetBuffer());
        }
    }

    public async Task WriteAsync(string key, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serializedToken);
        cancellationToken.ThrowIfCancellationRequested();
        var tokenPath = GetPath(key);
        Directory.CreateDirectory(_root);
        var temporaryPath = Path.Combine(_root, $".{key}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(serializedToken, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, tokenPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        if (File.Exists(path)) File.Delete(path);

        return Task.CompletedTask;
    }

    private string GetPath(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _))
            throw new ArgumentException("An OAuth token key must be an account id in N format.", nameof(key));

        return Path.Combine(_root, $"{key}.json");
    }
}
