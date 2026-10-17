#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>Shared validation and JSON for the device credential stores.</summary>
public abstract class PushRelayDeviceCredentialStoreBase : IPushRelayDeviceCredentialStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    protected ILogger Logger { get; } = Log.ForContext<PushRelayDeviceCredentialStoreBase>();

    public async Task<PushRelayDeviceCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;

            try
            {
                var credential = JsonSerializer.Deserialize(bytes, PushRelayJsonContext.Default.PushRelayDeviceCredential);
                if (credential is null || credential.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(credential.Secret)
                    || string.IsNullOrWhiteSpace(credential.Platform) || !Enum.IsDefined(credential.AttestationKind))
                {
                    await DeleteCoreAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }

                return credential;
            }
            catch (JsonException)
            {
                await DeleteCoreAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PushRelayDeviceCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(credential, PushRelayJsonContext.Default.PushRelayDeviceCredential);
            try { await WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DeleteCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    protected abstract Task<byte[]?> ReadAsync(CancellationToken cancellationToken);
    protected abstract Task WriteAsync(byte[] bytes, CancellationToken cancellationToken);
    protected abstract Task DeleteCoreAsync(CancellationToken cancellationToken);
}

/// <summary>The credential in the platform secure item store (Keychain on Apple, protected files on Android).</summary>
public sealed class SecureItemPushRelayDeviceCredentialStore(ISecureItemStore store) : PushRelayDeviceCredentialStoreBase
{
    public const string Scope = "wino.push-relay";
    public const string Account = "device-credential";

    protected override Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(store.Read(Scope, Account));

    protected override Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        store.Write(Scope, Account, bytes, SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);
        return Task.CompletedTask;
    }

    protected override Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        store.Delete(Scope, Account);
        return Task.CompletedTask;
    }
}

/// <summary>The credential in a file under the application data folder, protected with the platform secret protector (DPAPI on Windows).</summary>
public sealed class ProtectedFilePushRelayDeviceCredentialStore(ISecretProtector protector, IApplicationConfiguration configuration) : PushRelayDeviceCredentialStoreBase
{
    public const string FolderName = "PushRelay";
    public const string FileName = "device-credential.bin";
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("wino.push-relay.device-credential.v1");

    private string Root => Path.Combine(configuration.ApplicationDataFolderPath, FolderName);
    private string FilePath => Path.Combine(Root, FileName);

    protected override async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;
        byte[] protectedBytes;
        try { protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }

        try { return protector.Unprotect(protectedBytes, Context); }
        catch (CryptographicException)
        {
            // Another user or machine wrote it; the device registers again.
            return null;
        }
    }

    protected override async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var protectedBytes = protector.Protect(bytes, Context);
        var temporary = Path.Combine(Root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, FilePath, overwrite: true);
    }

    protected override Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        try { File.Delete(FilePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}

/// <summary>Heads without a platform attestation service (Windows, macOS). The API accepts <c>none</c> for desktop platforms.</summary>
public sealed class NoDeviceAttestationProvider : IDeviceAttestationProvider
{
    public DeviceAttestationKind Kind => DeviceAttestationKind.None;
    public bool IsSupported => true;
    public Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default) => Task.FromResult(DeviceAttestation.None);
    public Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
