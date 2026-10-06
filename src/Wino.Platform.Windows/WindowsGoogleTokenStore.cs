using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Windows;

/// <summary>Preserves the existing Windows plaintext Gmail token file representation.</summary>
public sealed class WindowsGoogleTokenStore : IGoogleTokenStore
{
    private readonly string _root;

    public WindowsGoogleTokenStore(IApplicationConfiguration configuration)
        => _root = AuthenticationTokenStorePaths.GetGmailTokenStorePath(configuration);

    public async Task<byte[]> ReadAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(credentialKey);
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

    public async Task WriteAsync(string credentialKey, byte[] serializedToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokenPath = GetPath(credentialKey);
        Directory.CreateDirectory(_root);
        var temporaryPath = Path.Combine(_root, $".{credentialKey}.{Guid.NewGuid():N}.tmp");

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

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(credentialKey);
        if (File.Exists(path)) File.Delete(path);

        return Task.CompletedTask;
    }

    private string GetPath(string credentialKey)
    {
        if (credentialKey != "default" && !Guid.TryParseExact(credentialKey, "N", out _))
            throw new ArgumentException("A token key must be an account id or default.", nameof(credentialKey));

        return Path.Combine(_root, $"{credentialKey}.json");
    }
}
