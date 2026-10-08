using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// One-shot startup jobs for account profile pictures: migrate legacy base64 rows to files,
/// then backfill missing pictures from the provider.
/// </summary>
public sealed class AccountProfilePictureMaintenance
{
    private readonly IDatabaseService _databaseService;
    private readonly IPictureStorageService _pictureStorage;
    private readonly IMessenger _messenger;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly ILogger _logger = Log.ForContext<AccountProfilePictureMaintenance>();
    private int _isBackfillRunning;

    public AccountProfilePictureMaintenance(
        IDatabaseService databaseService,
        IPictureStorageService pictureStorage,
        IMessenger messenger,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager)
    {
        _databaseService = databaseService;
        _pictureStorage = pictureStorage;
        _messenger = messenger;
        _accountService = accountService;
        _synchronizationManager = synchronizationManager;
    }

    /// <summary>
    /// Moves legacy base64 profile pictures out of the account rows into picture files.
    /// </summary>
    public async Task MigrateLegacyAsync()
    {
        var accounts = await _databaseService.Connection.Table<MailAccount>()
            .Where(account => account.Base64ProfilePictureData != null && account.Base64ProfilePictureData != string.Empty)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var account in accounts)
        {
            try
            {
                if (!account.ProfilePictureFileId.HasValue)
                {
                    var legacyBytes = Convert.FromBase64String(account.Base64ProfilePictureData);
                    account.ProfilePictureFileId = await _pictureStorage
                        .SavePictureAsync(PictureKind.AccountProfile, legacyBytes)
                        .ConfigureAwait(false);
                }

                account.Base64ProfilePictureData = string.Empty;
                account.IsProfilePictureBackfillComplete = account.ProfilePictureFileId.HasValue;
                await _databaseService.Connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);
                _messenger.Send(new AccountUpdatedMessage(account));
            }
            catch (FormatException ex)
            {
                _logger.Warning(ex, "Discarding invalid legacy profile picture for account {AccountId}", account.Id);
                account.Base64ProfilePictureData = string.Empty;
                await _databaseService.Connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);
                _messenger.Send(new AccountUpdatedMessage(account));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to migrate legacy profile picture for account {AccountId}", account.Id);
            }
        }
    }

    /// <summary>
    /// Fetches profile pictures for Gmail and Outlook accounts that have none on disk yet.
    /// </summary>
    public async Task BackfillAsync()
    {
        if (Interlocked.Exchange(ref _isBackfillRunning, 1) != 0)
            return;

        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var eligibleAccounts = accounts
                .Where(account => account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
                .Where(account => !account.IsProfilePictureBackfillComplete ||
                                  account.ProfilePictureFileId is { } fileId &&
                                  _pictureStorage.GetPicturePath(PictureKind.AccountProfile, fileId) == null)
                .OrderBy(account => account.Order)
                .ToList();

            foreach (var account in eligibleAccounts)
            {
                try
                {
                    var result = await _synchronizationManager.SynchronizeProfileAsync(account.Id).ConfigureAwait(false);
                    if (result.ProfileInformation != null)
                        await _accountService.UpdateProfileInformationAsync(account.Id, result.ProfileInformation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Profile picture backfill failed for account {AccountId}", account.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Account profile picture backfill could not start.");
        }
        finally
        {
            Volatile.Write(ref _isBackfillRunning, 0);
        }
    }
}
