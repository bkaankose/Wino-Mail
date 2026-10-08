using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Intelligence.ConsoleApp.Hosting;

/// <summary>
/// Does what App.HandleMailSynchronizationRequestedAsync does for mail: runs the synchronization
/// through the shared manager, announces AccountSynchronizationCompleted (which is what starts
/// automatic intelligence processing of new mail), and clears a stale credentials warning.
/// Synchronizers themselves ask for follow-up runs with NewMailSynchronizationRequested, so those
/// are handled here too.
/// </summary>
internal sealed class SynchronizationHost : IDisposable
{
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAccountService _accountService;
    private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _accountGates = new();
    private readonly ConcurrentDictionary<Guid, string> _lastProgress = new();
    private Stopwatch? _progressClock;

    public SynchronizationHost(ISynchronizationManager synchronizationManager, IAccountService accountService)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;

        _messenger.Register<SynchronizationHost, NewMailSynchronizationRequested>(this,
            static (host, message) => _ = host.HandleRequestedAsync(message.Options));
        _messenger.Register<SynchronizationHost, AccountSynchronizationProgressUpdatedMessage>(this,
            static (host, message) => host.PrintProgress(message.Progress));
    }

    /// <summary>Prints progress lines only while a caller is watching a synchronization.</summary>
    public bool IsWatching => _progressClock is not null;

    public async Task<MailSynchronizationResult> SynchronizeAsync(
        MailSynchronizationOptions options,
        CancellationToken cancellationToken)
    {
        _progressClock = Stopwatch.StartNew();
        try
        {
            return await RunAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progressClock = null;
            _lastProgress.Clear();
        }
    }

    private async Task HandleRequestedAsync(MailSynchronizationOptions options)
    {
        ConsoleOutput.Muted($"[sync] Follow-up {options.Type} requested by the synchronizer.");
        var result = await RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        ConsoleOutput.Muted($"[sync] Follow-up {options.Type} finished: {result.CompletedState}.");
    }

    private async Task<MailSynchronizationResult> RunAsync(MailSynchronizationOptions options, CancellationToken cancellationToken)
    {
        var gate = _accountGates.GetOrAdd(options.AccountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        MailSynchronizationResult result;
        try
        {
            result = await Task.Run(() => _synchronizationManager.SynchronizeMailAsync(options, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = MailSynchronizationResult.Canceled;
        }
        catch (Exception exception)
        {
            result = MailSynchronizationResult.Failed(exception);
        }
        finally
        {
            gate.Release();
        }

        _messenger.Send(new AccountSynchronizationCompleted(
            options.AccountId,
            result.CompletedState,
            options.GroupedSynchronizationTrackingId,
            options.Type));

        if (result.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
            await ClearInvalidCredentialAttentionAsync(options.AccountId).ConfigureAwait(false);

        return result;
    }

    private async Task ClearInvalidCredentialAttentionAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account?.AttentionReason == AccountAttentionReason.InvalidCredentials)
            await _accountService.ClearAccountAttentionAsync(accountId).ConfigureAwait(false);
    }

    private void PrintProgress(AccountSynchronizationProgress progress)
    {
        var clock = _progressClock;
        if (clock is null || progress.Category != SynchronizationProgressCategory.Mail)
            return;

        var text = progress.IsIndeterminate || progress.TotalUnits == 0
            ? $"{progress.State} {progress.Status}".Trim()
            : $"{progress.State} {progress.CompletedUnits}/{progress.TotalUnits} ({progress.ProgressPercentage:0}%) {progress.Status}".Trim();

        // Progress fires per item; only a change in what would be printed is worth a line.
        if (_lastProgress.TryGetValue(progress.AccountId, out var previous) && previous == text)
            return;

        _lastProgress[progress.AccountId] = text;
        ConsoleOutput.Timeline(clock, $"[progress] {text}", ConsoleColor.DarkCyan);
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        foreach (var gate in _accountGates.Values)
            gate.Dispose();
    }
}
