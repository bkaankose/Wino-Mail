#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Platform storage for this install's relay socket key (desktop only). The key never roams: Apple keeps it in the
/// keychain as "this device only", Windows in a DPAPI-protected file. Heads that register no store have no socket.
/// </summary>
public interface IPushRelaySocketKeyStore
{
    /// <summary>Returns the stored key, or generates, stores and returns a new one when none is usable.</summary>
    Task<PushRelaySocketDeviceKey> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the stored key without creating one; null when none is stored.</summary>
    Task<PushRelaySocketDeviceKey?> LoadAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}
