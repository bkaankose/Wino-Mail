#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Decides, once per install, how the relay is introduced. A fresh install silently starts on relay-based sync with the
/// current privacy policy recorded. An install that already had accounts before this version keeps timer-based sync
/// and gets the one-time introduction window (Windows), where choosing relay-based sync goes through the policy.
/// </summary>
public interface IRelayIntroductionService
{
    /// <summary>
    /// Runs the first-launch decision when needed and reports whether the introduction window is still pending.
    /// Always false on platforms without a sync method choice.
    /// </summary>
    Task<bool> IsIntroductionPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>The recorded policy version is missing or older than the current one; choosing relay-based sync must show the policy first.</summary>
    bool IsPolicyAcceptanceRequired { get; }

    /// <summary>Records the current policy version.</summary>
    void AcceptPolicy();

    /// <summary>The user kept timer-based sync, or closed the window; the introduction is done.</summary>
    void CompleteWithTimer();

    /// <summary>The user accepted the policy and chose relay-based sync; the introduction is done and the relay pass starts.</summary>
    Task CompleteWithRelayAsync(CancellationToken cancellationToken = default);
}
