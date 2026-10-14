#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.Windows.PushRelay;

/// <summary>
/// Stores this install's relay device identity in a local, non-roaming file under the application data folder.
/// The identity holds no secret, so it is not encrypted.
/// </summary>
public sealed class WindowsPushRelayDeviceIdentityStore : IPushRelayDeviceIdentityStore
{
    private const string FolderName = "PushRelay";
    private const string FileName = "device-identity.json";

    private readonly IApplicationConfiguration _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsPushRelayDeviceIdentityStore(IApplicationConfiguration configuration)
        => _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    private string Root => Path.Combine(_configuration.ApplicationDataFolderPath, FolderName);
    private string FilePath => Path.Combine(Root, FileName);

    public async Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            PushRelayDeviceIdentity? identity;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, bufferSize: 4096, useAsync: true);
                identity = await JsonSerializer.DeserializeAsync(stream,
                    WindowsPushRelayJsonContext.Default.PushRelayDeviceIdentity, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                identity = null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            if (identity is null || identity.DeviceId == Guid.Empty || identity.WinoUserId == Guid.Empty
                || string.IsNullOrWhiteSpace(identity.Platform))
            {
                TryDelete(path);
                return null;
            }

            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = Root;
            Directory.CreateDirectory(root);
            var temporaryPath = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, identity,
                        WindowsPushRelayJsonContext.Default.PushRelayDeviceIdentity, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, Path.Combine(root, FileName), overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = FilePath;
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
