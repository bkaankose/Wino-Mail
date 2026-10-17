using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.PushRelay.Socket.KeyStores;

namespace Wino.Relay.ConsoleApp;

/// <summary>
/// The console's own relay device: identity and socket key in the console folder, so the app's device on this
/// machine is never touched. The key file is plain PKCS#8, acceptable for a developer tool.
/// </summary>
internal sealed class ConsoleIdentityStore(string folder) : IPushRelayDeviceIdentityStore
{
    private string FilePath => Path.Combine(folder, "device-identity.json");

    public async Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var identity = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(FilePath, cancellationToken), ConsoleJsonContext.Default.PushRelayDeviceIdentity);
            return identity is { DeviceId: var id } && id != Guid.Empty ? identity : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(identity, ConsoleJsonContext.Default.PushRelayDeviceIdentity), cancellationToken);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        return Task.CompletedTask;
    }
}

internal sealed class ConsoleKeyStore(string folder) : PushRelaySocketKeyStoreBase
{
    private string FilePath => Path.Combine(folder, "socket-key.p8");

    protected override async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
        => File.Exists(FilePath) ? await File.ReadAllBytesAsync(FilePath, cancellationToken) : null;

    protected override async Task WriteAsync(byte[] pkcs8PrivateKey, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(FilePath, pkcs8PrivateKey, cancellationToken);
    }

    protected override Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        return Task.CompletedTask;
    }
}
