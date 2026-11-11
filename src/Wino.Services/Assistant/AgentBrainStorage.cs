#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Security;

namespace Wino.Services.Assistant;

/// <summary>
/// Where the brain lives on this device: one file under the application data folder, encrypted (AES-256-GCM) with a
/// random per-device key kept in the secure item store (the Keychain on Apple; a protected file elsewhere) under the
/// <c>agent-brain</c> scope. The brain sync secrets (the derived key, the sync state) are items of the same scope.
/// The runtime owns the document format; this only keeps bytes.
/// </summary>
public sealed class AgentBrainStorage : IAgentBrainStorage
{
    public const string Scope = "agent-brain";
    private const string DeviceKeyItem = "device-key";
    private const string FolderName = "agent-brain";
    private const string FileName = "brain.bin";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly ISecureItemStore _secureItems;
    private readonly string _folder;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<AgentBrainStorage>();

    public AgentBrainStorage(IApplicationConfiguration configuration, ISecureItemStore? secureItems, ISecretProtector? protector)
    {
        _folder = Path.Combine(configuration.ApplicationDataFolderPath, FolderName);
        _secureItems = secureItems
            ?? (protector is not null ? new ProtectedFileSecureItemStore(Path.Combine(_folder, "secure-items"), protector)
                : throw new InvalidOperationException("The agent brain needs a secure item store or a secret protector."));
    }

    private string FilePath => Path.Combine(_folder, FileName);

    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath)) return null;
            var content = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);
            if (content.Length < NonceLength + TagLength) return null;
            var key = GetOrCreateDeviceKey();
            try
            {
                var plain = new byte[content.Length - NonceLength - TagLength];
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(content.AsSpan(0, NonceLength), content.AsSpan(NonceLength, plain.Length), content.AsSpan(NonceLength + plain.Length, TagLength), plain);
                return plain;
            }
            catch (CryptographicException ex)
            {
                // The device key changed under the file (a restored backup): the brain starts empty rather than failing every start.
                _logger.Warning(ex, "The agent brain file could not be opened with the device key; starting empty.");
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally { _fileLock.Release(); }
    }

    public async Task WriteAsync(byte[] document, CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folder);
            var key = GetOrCreateDeviceKey();
            try
            {
                var content = new byte[NonceLength + document.Length + TagLength];
                RandomNumberGenerator.Fill(content.AsSpan(0, NonceLength));
                using var aes = new AesGcm(key, TagLength);
                aes.Encrypt(content.AsSpan(0, NonceLength), document, content.AsSpan(NonceLength, document.Length), content.AsSpan(NonceLength + document.Length, TagLength));
                var temp = FilePath + ".tmp";
                await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
                File.Move(temp, FilePath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally { _fileLock.Release(); }
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        finally { _fileLock.Release(); }
    }

    public byte[]? ReadSecret(string name) => _secureItems.Read(Scope, name);

    public void WriteSecret(string name, byte[] value) => _secureItems.Write(Scope, name, value);

    public void DeleteSecret(string name) => _secureItems.Delete(Scope, name);

    private byte[] GetOrCreateDeviceKey()
    {
        if (_secureItems.Read(Scope, DeviceKeyItem) is { Length: 32 } existing) return existing;
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        if (_secureItems.TryAdd(Scope, DeviceKeyItem, key)) return key;
        return _secureItems.Read(Scope, DeviceKeyItem) ?? key;
    }
}
