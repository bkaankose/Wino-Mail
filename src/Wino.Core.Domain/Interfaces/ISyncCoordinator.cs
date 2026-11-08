#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The one owner of how mail reaches the app: the effective sync mode, which accounts are covered live (relay
/// mailbox over the desktop socket, or IMAP IDLE), the intervals the timer loops use, when a lost or recovered
/// socket deserves a bounded catch-up, and when a stalled relay registration should try again. It implements
/// <see cref="IRelayHealth"/> with hysteresis so the shell indicator and the runtime see a stable signal, and it
/// logs every decision once, on change, to the application log.
/// </summary>
public interface ISyncCoordinator : IRelayHealth, IAsyncDisposable
{
    /// <summary>The current immutable view. Safe to read from any thread.</summary>
    SyncCoordinationSnapshot Snapshot { get; }

    /// <summary>
    /// Decides the sync method for this install (upgraded installs keep the timer until the user chooses), computes
    /// the initial coverage and subscribes to its inputs. Runs during runtime initialization after the database is
    /// ready and before any synchronizer exists.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the timer tick must synchronize the account: no live coverage right now and nothing blocks it. An
    /// account that needs attention is never synchronized by the timer until the user fixes it.
    /// </summary>
    bool ShouldRunTimerSync(Guid accountId);

    /// <summary>The mail loop interval: the user's setting on desktop, the default elsewhere. Never shorter than one minute.</summary>
    TimeSpan MailSyncInterval { get; }

    /// <summary>The calendar loop interval: the user's setting on desktop, the default elsewhere. Never shorter than one minute.</summary>
    TimeSpan CalendarSyncInterval { get; }

    /// <summary>
    /// How often a quota-bound source may pull from its provider. Google Tasks shares one daily request quota across
    /// every Wino installation, and Google contacts keep the same schedule.
    /// </summary>
    TimeSpan QuotaWindow { get; }

    /// <summary>
    /// True when the account's mode reads from a provider with a quota shared by every installation: Google Tasks and
    /// Google contacts. Quota-bound sources synchronize once per <see cref="QuotaWindow"/> and never at launch; every
    /// other remote source follows <see cref="CalendarSyncInterval"/>.
    /// </summary>
    bool IsQuotaBound(MailAccount account, WinoApplicationMode mode);

    /// <summary>
    /// When the account's Google Tasks may pull again, or null when a pull is allowed now or its To Do is not
    /// quota-bound. The window runs from the later of the last successful pull and the last attempt in this session.
    /// </summary>
    Task<DateTimeOffset?> GetNextQuotaTaskPullUtcAsync(MailAccount account);

    /// <summary>
    /// The hard gate for every Google Tasks pull. Returns false while the window is open; otherwise stamps the attempt
    /// (so a failing pull is not retried before the window closes) and returns true. Sources that are not quota-bound
    /// always pass.
    /// </summary>
    Task<bool> TryBeginQuotaTaskPullAsync(MailAccount account);

    /// <summary>The runtime reports each finished automatic mail synchronization; catch-up decisions use the last success.</summary>
    void ReportTimerMailSyncCompleted(Guid accountId, bool succeeded, DateTimeOffset completedUtc);

    /// <summary>A user-visible moment passed; the coordinator decides whether the relay registration or socket should try again now.</summary>
    void RequestRelayRetry(RelayRetryTrigger trigger);

    /// <summary>Raised on a background thread when the runtime should run one bounded mail synchronization now.</summary>
    event EventHandler<SyncCatchUpReason>? CatchUpRequested;
}
