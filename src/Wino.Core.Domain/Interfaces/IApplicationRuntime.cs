using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IApplicationRuntime
{
    ApplicationRuntimeState State { get; }
    event EventHandler<ApplicationRuntimeState> StateChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SynchronizeCreatedAccountAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one bounded synchronization pass across all accounts and returns when it finishes:
    /// inbox mail, calendar metadata, and the task and contact modes when their six-hour window is due.
    /// It initializes the runtime when needed but never starts the periodic loops, so hosts that only
    /// get short background windows (for example a mobile background refresh task) can call it.
    /// Cancelling the token stops the pass.
    /// </summary>
    Task SynchronizeOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one bounded inbox-only mail synchronization for one account and returns when it finishes.
    /// Returns false when the account is unknown, has no mail access, or the synchronization did not succeed.
    /// It initializes the runtime when needed but never starts the periodic loops (used for relay pushes).
    /// </summary>
    Task<bool> SynchronizeAccountInboxAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the change requests waiting in the synchronizer queues (mail, folder and category actions,
    /// calendar, contact and task changes) to their servers and returns when the pass finishes. Only accounts
    /// with queued requests are touched; nothing is downloaded beyond what executing the requests needs.
    /// It initializes the runtime when needed but never starts the periodic loops. Returns true when no
    /// queued request is left afterwards. The queues live in memory, so a new process has nothing to send.
    /// </summary>
    Task<bool> FlushQueuedRequestsAsync(CancellationToken cancellationToken = default);
}
