using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Shared resolution step for the thin Exchange orchestrator services (rules, public folders, the online
/// archive): fetch the account's synchronizer and refuse the call when it cannot serve the feature.
/// </summary>
internal static class SynchronizerCapabilityGate
{
    /// <summary>
    /// Resolves the synchronizer for <paramref name="accountId"/> and returns it only when
    /// <paramref name="isCapable"/> holds, so callers never have to null-check or capability-check again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account has no synchronizer.</exception>
    /// <exception cref="NotSupportedException">The synchronizer does not support the feature.</exception>
    public static async Task<IWinoSynchronizerBase> RequireAsync(
        ISynchronizerFactory synchronizerFactory,
        Guid accountId,
        Func<IWinoSynchronizerBase, bool> isCapable,
        string notSupportedMessage)
    {
        var synchronizer = await synchronizerFactory.GetAccountSynchronizerAsync(accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No synchronizer is available for account {accountId}.");

        if (!isCapable(synchronizer))
            throw new NotSupportedException(notSupportedMessage);

        return synchronizer;
    }
}
