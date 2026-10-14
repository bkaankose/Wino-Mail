#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Platform storage for this install's relay device id. Apple stores it in the keychain as "this device only",
/// Windows in a local file. It must never roam or travel with a profile export. Load returns null when nothing usable is stored.
/// </summary>
public interface IPushRelayDeviceIdentityStore
{
    Task<PushRelayDeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PushRelayDeviceIdentity identity, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
