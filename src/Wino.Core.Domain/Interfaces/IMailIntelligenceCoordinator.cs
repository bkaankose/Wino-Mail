#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Drives Classification-first, Enrichment-second processing from this device: selects messages,
/// sends them in batches on the streamed route and imports each result the moment it arrives.
/// The server stores nothing, so there is nothing to poll, acknowledge or resume: a batch that
/// does not finish leaves its messages unprocessed for the next run.
/// </summary>
public interface IMailIntelligenceCoordinator
{
    Task InitializeAsync();

    /// <summary>
    /// Called when the add-on becomes active: sends the mails synchronized while it could not take
    /// work, and once per run removes jobs an earlier build left on the server.
    /// </summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every run; closing the streams makes the server stop and refund what it did not
    /// deliver. Imported artifacts are kept. Called when the add-on ends.
    /// </summary>
    Task CancelAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes the selected messages in batches. Runs in the background; progress is published
    /// as <see cref="Messaging.UI.MailIntelligenceJobChanged"/>. A run already going for the
    /// account wins and this call does nothing.
    /// </summary>
    Task StartProcessingAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default);

    /// <summary>Processes one message synchronously and imports both returned artifacts.</summary>
    Task ProcessMessageAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Cancels the account's run, closing its streams.</summary>
    Task CancelAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);

    MailIntelligenceJobSnapshot GetJobSnapshot(Guid localMailAccountId);
    Task<MailIntelligenceAccountState> GetStateAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task<MailMessageIntelligenceState> GetMessageStateAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default);

    Task DeleteLocalIntelligenceAsync(Guid localMailAccountId, CancellationToken cancellationToken = default);
    Task ResetLocalStateAsync(CancellationToken cancellationToken = default);
}
