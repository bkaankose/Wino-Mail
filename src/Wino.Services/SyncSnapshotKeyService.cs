#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services;

/// <summary>
/// Derives snapshot keys and keeps the derived key per Wino user in a DPAPI-protected file, the
/// same way DAV credentials are kept. The password or passphrase is never written anywhere.
/// </summary>
public sealed class SyncSnapshotKeyService : ISyncSnapshotKeyService
{
    // RFC 9106 second recommended profile. Written into every snapshot header, so they can change
    // later without a format change.
    public const int DefaultMemoryKiB = 65536;
    public const int DefaultIterations = 3;
    public const int DefaultParallelism = 4;

    private const byte FileVersion = 1;
    private const string FolderName = "sync-snapshot";

    private readonly string _rootFolder;
    private readonly ILogger _logger = Log.ForContext<SyncSnapshotKeyService>();

    public SyncSnapshotKeyService(IApplicationConfiguration configuration)
    {
        _rootFolder = Path.Combine(configuration.ApplicationDataFolderPath, FolderName);
    }

    public SyncSnapshotKeyParameters CreateDefaultParameters(Guid? userId, byte keySource)
        => new(
            keySource,
            DefaultMemoryKiB,
            DefaultIterations,
            DefaultParallelism,
            userId is { } id ? SyncSnapshotCryptography.CreateUserSalt(id) : SyncSnapshotCryptography.CreateSalt());

    public async Task<SyncSnapshotKey?> GetCachedKeyAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(userId);
        if (!File.Exists(path)) return null;

        byte[]? clear = null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            clear = Unprotect(protectedBytes, userId);

            return Parse(clear);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Cached sync snapshot key could not be read; it will be derived again.");
            return null;
        }
        finally
        {
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task<SyncSnapshotKey> DeriveAsync(string secret, SyncSnapshotKeyParameters parameters, CancellationToken cancellationToken = default)
    {
        var key = await SyncSnapshotCryptography.DeriveKeyAsync(secret, parameters, cancellationToken).ConfigureAwait(false);

        return new SyncSnapshotKey(parameters, key);
    }

    public async Task RememberAsync(Guid userId, SyncSnapshotKey key, CancellationToken cancellationToken = default)
    {
        var clear = Serialize(key);
        try
        {
            Directory.CreateDirectory(_rootFolder);

            var path = GetPath(userId);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporaryPath, Protect(clear, userId), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task RememberPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)) return;

        var parameters = CreateDefaultParameters(userId, SyncSnapshotFormat.KeySourceAccountPassword);
        var key = await DeriveAsync(password, parameters, cancellationToken).ConfigureAwait(false);

        await RememberAsync(userId, key, cancellationToken).ConfigureAwait(false);
    }

    public Task ForgetAsync(Guid userId)
    {
        try
        {
            var path = GetPath(userId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Cached sync snapshot key could not be removed.");
        }

        return Task.CompletedTask;
    }

    private string GetPath(Guid userId) => Path.Combine(_rootFolder, $"{userId:N}.key");

    private static byte[] Entropy(Guid userId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.SyncSnapshotKey.{userId:D}"));

#pragma warning disable CA1416 // DPAPI is Windows-only, and so is the app.
    private static byte[] Protect(byte[] clear, Guid userId)
        => ProtectedData.Protect(clear, Entropy(userId), DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] protectedBytes, Guid userId)
        => ProtectedData.Unprotect(protectedBytes, Entropy(userId), DataProtectionScope.CurrentUser);
#pragma warning restore CA1416

    // version u8 | keySource u8 | memoryKiB u32 | iterations u32 | parallelism u8 | salt 16 | key 32
    private static byte[] Serialize(SyncSnapshotKey key)
    {
        var buffer = new byte[1 + 1 + 4 + 4 + 1 + SyncSnapshotFormat.SaltLength + SyncSnapshotCryptography.KeyLength];
        var span = buffer.AsSpan();
        span[0] = FileVersion;
        span[1] = key.Parameters.KeySource;
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)key.Parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(span[6..], (uint)key.Parameters.Iterations);
        span[10] = (byte)key.Parameters.Parallelism;
        key.Parameters.Salt.CopyTo(span[11..]);
        key.Key.CopyTo(span[(11 + SyncSnapshotFormat.SaltLength)..]);
        return buffer;
    }

    private static SyncSnapshotKey? Parse(ReadOnlySpan<byte> clear)
    {
        if (clear.Length != 1 + 1 + 4 + 4 + 1 + SyncSnapshotFormat.SaltLength + SyncSnapshotCryptography.KeyLength || clear[0] != FileVersion)
        {
            return null;
        }

        var parameters = new SyncSnapshotKeyParameters(
            clear[1],
            (int)BinaryPrimitives.ReadUInt32LittleEndian(clear[2..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(clear[6..]),
            clear[10],
            clear.Slice(11, SyncSnapshotFormat.SaltLength).ToArray());

        return new SyncSnapshotKey(parameters, clear.Slice(11 + SyncSnapshotFormat.SaltLength, SyncSnapshotCryptography.KeyLength).ToArray());
    }
}
