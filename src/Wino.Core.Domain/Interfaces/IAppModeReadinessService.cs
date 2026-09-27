using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Decides whether an app mode's main page can create or change anything: no account,
/// the mode turned off everywhere, a sign-in pending, or a first sync still running.
/// </summary>
public interface IAppModeReadinessService
{
    /// <summary>
    /// Raised on a background thread when accounts, capabilities or synchronization change
    /// in a way that can move a mode between states. Re-evaluate on receipt.
    /// </summary>
    event EventHandler ReadinessInvalidated;

    Task<AppModeReadiness> GetReadinessAsync(WinoApplicationMode mode, CancellationToken cancellationToken = default);
}
