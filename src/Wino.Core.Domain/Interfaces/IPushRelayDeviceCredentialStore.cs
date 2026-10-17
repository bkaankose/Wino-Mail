#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Secure platform storage for <see cref="PushRelayDeviceCredential"/> (Keychain on Apple, protected files elsewhere).
/// Load returns null when nothing usable is stored.
/// </summary>
public interface IPushRelayDeviceCredentialStore
{
    Task<PushRelayDeviceCredential?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PushRelayDeviceCredential credential, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}
