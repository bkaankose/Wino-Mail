#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Drives Classification-first, Summarization-second processing from this device: selects messages, uploads
/// one encrypted job per mailbox, polls, imports each stage, and acknowledges each stage
/// only after its import has committed.
/// </summary>
public interface IMailIntelligenceCoordinator
{
    Task InitializeAsync();

    /// <summary>
    /// Submits the selected messages. A selection larger than the server's per-job limit
    /// is split across several jobs, which may run concurrently for one mailbox.
    /// </summary>
    Task StartProcessingAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default);

    /// <summary>Processes one message synchronously and imports both returned artifacts.</summary>
    Task ProcessMessageAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Polls every unfinished job once and imports whatever is ready.</summary>
    Task PollAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    MailIntelligenceJobSnapshot GetJobSnapshot(Guid localMailAccountId);
    Task<MailIntelligenceAccountState> GetStateAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Models.Intelligence.MailIntelligenceJobState>> GetJobsAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task<MailMessageIntelligenceState> GetMessageStateAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);

    Task DeleteLocalIntelligenceAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task ResetLocalStateAsync(CancellationToken cancellationToken = default);
}
