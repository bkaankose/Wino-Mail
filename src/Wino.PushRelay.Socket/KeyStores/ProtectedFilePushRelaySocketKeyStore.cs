#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.PushRelay.Socket.KeyStores;

/// <summary>
/// The socket key in a file under the application data folder, protected with the platform secret protector
/// (DPAPI for the current user on Windows). The file is non-roaming and lives beside the relay device identity.
/// </summary>
public sealed class ProtectedFilePushRelaySocketKeyStore : PushRelaySocketKeyStoreBase
{
    public const string FolderName = "PushRelay";
    public const string FileName = "socket-key.bin";
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("wino.push-relay.socket-key.v1");

    private readonly ISecretProtector _protector;
    private readonly IApplicationConfiguration _configuration;

    public ProtectedFilePushRelaySocketKeyStore(ISecretProtector protector, IApplicationConfiguration configuration)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    private string Root => Path.Combine(_configuration.ApplicationDataFolderPath, FolderName);
    private string FilePath => Path.Combine(Root, FileName);

    protected override async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;

        byte[] protectedBytes;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedBytes, Context);
        }
        catch (CryptographicException)
        {
            // Another user or machine wrote it; a new key replaces it.
            return null;
        }
    }

    protected override async Task WriteAsync(byte[] pkcs8PrivateKey, CancellationToken cancellationToken)
    {
        var root = Root;
        Directory.CreateDirectory(root);
        var temporaryPath = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        var protectedBytes = _protector.Protect(pkcs8PrivateKey, Context);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    protected override Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(FilePath)) File.Delete(FilePath);
        return Task.CompletedTask;
    }
}
