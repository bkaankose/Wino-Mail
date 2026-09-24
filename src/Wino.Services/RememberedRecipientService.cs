using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Services;

/// <summary>
/// Thin orchestrator over each account's mail synchronizer for the remembered-recipient list, in the
/// same shape as <see cref="GlobalAddressListService"/>: it never throws except for cancellation, so a
/// provider without a list, a missing synchronizer or a failed read all leave the composer with
/// contacts and the directory.
///
/// What it adds over the GAL service is memory. The list is whole rather than a query, so it is read
/// once per account and filtered here; asking the server per keystroke would be unusable.
/// </summary>
public class RememberedRecipientService : IRememberedRecipientService
{
    private sealed class AccountList
    {
        public IReadOnlyList<RememberedRecipient> Recipients { get; set; } = [];

        /// <summary>
        /// Set once this account's answer is known, and an empty list is an answer: the provider keeps
        /// none, or the read failed. Without it a miss is never remembered, so every keystroke on an
        /// account that has no list resolves the synchronizer again - and a mailbox whose read failed
        /// is asked again per keystroke.
        /// </summary>
        public bool Settled { get; set; }

        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly IMailSynchronizerFactory _synchronizerFactory;
    private readonly ILogger _logger = Log.ForContext<RememberedRecipientService>();
    private readonly ConcurrentDictionary<Guid, AccountList> _lists = new();

    public RememberedRecipientService(IMailSynchronizerFactory synchronizerFactory)
    {
        _synchronizerFactory = synchronizerFactory;
    }

    public async Task<IReadOnlyList<RememberedRecipient>> SuggestAsync(Guid accountId, string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        var list = await LoadAsync(accountId, cancellationToken).ConfigureAwait(false);
        var needle = query.Trim();

        // The provider returned them best first, so taking them in order is taking the best ones.
        return list.Recipients
            .Where(r => Matches(r, needle))
            .Take(limit)
            .ToList();
    }

    public async Task RecordAsync(Guid accountId, IReadOnlyList<RememberedRecipient> recipients, CancellationToken cancellationToken = default)
    {
        if (recipients is null || recipients.Count == 0)
            return;

        try
        {
            var synchronizer = await _synchronizerFactory.GetSynchronizerAsync(accountId).ConfigureAwait(false);

            if (synchronizer is not { Capabilities.CanRememberRecipients: true })
                return;

            await synchronizer.RememberRecipientsAsync(recipients, cancellationToken).ConfigureAwait(false);

            // The provider decides the weights, so the local copy is refreshed from it rather than
            // guessed at here.
            if (_lists.TryGetValue(accountId, out var list))
                list.Recipients = await synchronizer.GetRememberedRecipientsAsync(cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Losing what a send taught the list is a nuisance; failing the send over it is worse.
            _logger.Debug(exception, "Could not record remembered recipients for {AccountId}.", accountId);
        }
    }

    private static bool Matches(RememberedRecipient recipient, string needle)
        => Contains(recipient.Address, needle) || Contains(recipient.DisplayName, needle);

    private static bool Contains(string? value, string needle)
        => value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private async Task<AccountList> LoadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var list = _lists.GetOrAdd(accountId, _ => new AccountList());

        if (list.Settled)
            return list;

        await list.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (list.Settled)
                return list;

            var synchronizer = await _synchronizerFactory.GetSynchronizerAsync(accountId).ConfigureAwait(false);

            if (synchronizer is { Capabilities.CanRememberRecipients: true })
            {
                list.Recipients = await synchronizer.GetRememberedRecipientsAsync(cancellationToken).ConfigureAwait(false) ?? [];
                _logger.Information("Remembered recipients for {AccountId}: {Count}.", accountId, list.Recipients.Count);
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Debug(exception, "Could not read remembered recipients for {AccountId}.", accountId);
            return list;
        }
        finally
        {
            list.Settled = true;
            list.Gate.Release();
        }
    }
}
