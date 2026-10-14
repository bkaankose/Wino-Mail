#nullable enable
using System.Runtime.Versioning;
using System.Text.Json;
using Security;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Platform.Apple.Security;
using Wino.Services.PushRelay;

namespace Wino.Platform.Apple.PushRelay;

/// <summary>
/// The relay device identity as one Keychain item. "This device only" keeps an iCloud or encrypted-backup restore onto
/// another device from reusing the relay device id; "after first unlock" keeps it readable while a push wakes the app
/// in the background. Missing or unreadable JSON loads as null, so the service registers a new device.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios17.0")]
public sealed class AppleKeychainPushRelayDeviceIdentityStore(AppleKeychainStore keychain) : IPushRelayDeviceIdentityStore
{
    public const string Scope = "push-relay";
    public const string Account = "device-identity-v1";

    public Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = keychain.Read(Scope, Account);
        if (bytes is null || bytes.Length == 0) return Task.FromResult<PushRelayDeviceIdentity?>(null);
        try
        {
            var identity = JsonSerializer.Deserialize(bytes, PushRelayJsonContext.Default.PushRelayDeviceIdentity);
            if (identity is null || identity.DeviceId == Guid.Empty || string.IsNullOrEmpty(identity.Platform))
                return Task.FromResult<PushRelayDeviceIdentity?>(null);
            return Task.FromResult<PushRelayDeviceIdentity?>(identity);
        }
        catch (JsonException error)
        {
            Serilog.Log.Warning(error, "The stored relay device identity is unreadable; a new device will be registered.");
            return Task.FromResult<PushRelayDeviceIdentity?>(null);
        }
    }

    public Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity, PushRelayJsonContext.Default.PushRelayDeviceIdentity);
        keychain.Write(Scope, Account, bytes, SecAccessible.AfterFirstUnlockThisDeviceOnly);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keychain.Delete(Scope, Account);
        return Task.CompletedTask;
    }
}
