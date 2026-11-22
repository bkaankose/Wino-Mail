#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// A failed backfill leaves the stored range unchanged; the mail it already added stays, and running the change
/// again skips it.
/// </summary>
public sealed class MailSynchronizationRangeService : IMailSynchronizationRangeService
{
    private readonly ILogger _logger = Log.ForContext<MailSynchronizationRangeService>();
    private readonly ConcurrentDictionary<Guid, byte> _runningChanges = new();
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IMailProviderRegistry _providers;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IMessenger _messenger;

    public MailSynchronizationRangeService(
        IAccountService accountService,
        IFolderService folderService,
        IMailProviderRegistry providers,
        ISynchronizationManager synchronizationManager,
        IMessenger messenger)
    {
        _accountService = accountService;
        _folderService = folderService;
        _providers = providers;
        _synchronizationManager = synchronizationManager;
        _messenger = messenger;
    }

    public async Task<MailSynchronizationRangeStatus> GetStatusAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account == null)
            return MailSynchronizationRangeStatus.Unsupported;

        var traits = _providers.GetTraits(account.ProviderType);
        if (!traits.SupportsMailSynchronizationRangeChange || !account.IsMailAccessGranted)
            return MailSynchronizationRangeStatus.Unsupported with { CurrentRange = account.InitialSynchronizationRange };

        return new MailSynchronizationRangeStatus(
            true,
            await IsInitialSynchronizationCompletedAsync(account, traits.TracksMailChangesPerAccount).ConfigureAwait(false),
            _runningChanges.ContainsKey(accountId),
            account.InitialSynchronizationRange);
    }

    public async Task<bool> ChangeRangeAsync(Guid accountId, InitialSynchronizationRange target, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(accountId).ConfigureAwait(false);
        if (!status.IsSupported || !status.IsInitialSynchronizationCompleted || !target.IsWiderThan(status.CurrentRange))
        {
            _logger.Warning("Refused to change the mail synchronization range of {AccountId} from {Current} to {Target}", accountId, status.CurrentRange, target);
            return false;
        }

        if (!_runningChanges.TryAdd(accountId, 0))
            return false;

        _messenger.Send(new MailSynchronizationRangeChangeStateChanged(accountId, true));

        var succeeded = false;

        try
        {
            _logger.Information("Widening the mail synchronization range of {AccountId} from {Current} to {Target}", accountId, status.CurrentRange, target);

            var result = await _synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders,
                RangeBackfillTarget = target
            }, cancellationToken).ConfigureAwait(false);

            if (result.CompletedState == SynchronizationCompletedState.Success)
            {
                await _accountService.UpdateInitialSynchronizationRangeAsync(accountId, target).ConfigureAwait(false);
                succeeded = true;
            }
            else
            {
                _logger.Warning(result.Exception, "Widening the mail synchronization range of {AccountId} ended with {State}", accountId, result.CompletedState);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Widening the mail synchronization range of {AccountId} failed", accountId);
        }
        finally
        {
            _runningChanges.TryRemove(accountId, out _);
            _messenger.Send(new MailSynchronizationRangeChangeStateChanged(accountId, false));
        }

        _messenger.Send(succeeded
            ? new InfoBarMessageRequested(InfoBarMessageType.Success, Translator.AccountMail_SyncRange_Title, await GetSucceededMessageAsync(accountId, target).ConfigureAwait(false))
            : new InfoBarMessageRequested(InfoBarMessageType.Error, Translator.AccountMail_SyncRange_Title, Translator.AccountMail_SyncRange_Failed));

        return succeeded;
    }

    private async Task<string> GetSucceededMessageAsync(Guid accountId, InitialSynchronizationRange target)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

        return account?.GetMailSynchronizationCutoffUtc(target) is DateTime cutoffUtc
            ? string.Format(Translator.AccountMail_SyncRange_Succeeded, cutoffUtc.ToLocalTime().ToString("D", CultureInfo.CurrentUICulture))
            : Translator.AccountMail_SyncRange_SucceededEverything;
    }

    private async Task<bool> IsInitialSynchronizationCompletedAsync(MailAccount account, bool tracksChangesPerAccount)
    {
        if (tracksChangesPerAccount)
            return !string.IsNullOrEmpty(account.SynchronizationDeltaIdentifier);

        var folders = await _folderService.GetSynchronizationFoldersAsync(new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.FullFolders
        }).ConfigureAwait(false);

        return folders.Count > 0 && folders.All(folder => folder.LastSynchronizedDate.HasValue);
    }
}
