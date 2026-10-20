#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Keeps this device's app privacy policy acceptance and the signed-in Wino Account's record in step. The local
/// configuration value stays the per-device record; a newer local acceptance is uploaded to the account, and a newer
/// account acceptance is adopted locally. Network failures never block anything; the next sync point retries.
/// </summary>
public interface IAppPolicyAcceptanceSyncService
{
    /// <summary>
    /// Called after the local accepted version was written. Remembers <paramref name="source"/> (an
    /// <c>AppPolicyAcceptanceSources</c> value) for retries and starts a reconcile pass without waiting for it.
    /// </summary>
    void NotifyLocalAcceptance(string source);

    /// <summary>Compares the local and account acceptance and converges them. Never throws.</summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
