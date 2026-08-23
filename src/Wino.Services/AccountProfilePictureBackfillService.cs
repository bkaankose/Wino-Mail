using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class AccountProfilePictureBackfillService
{
    private readonly IAccountService _accountService;
    private readonly IAccountProfilePictureFileService _profilePictureFileService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly ILogger _logger = Log.ForContext<AccountProfilePictureBackfillService>();
    private int _isRunning;

    public AccountProfilePictureBackfillService(
        IAccountService accountService,
        IAccountProfilePictureFileService profilePictureFileService,
        ISynchronizationManager synchronizationManager)
    {
        _accountService = accountService;
        _profilePictureFileService = profilePictureFileService;
        _synchronizationManager = synchronizationManager;
    }

    public async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _isRunning, 1) != 0)
            return;

        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var eligibleAccounts = accounts
                .Where(account => account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
                .Where(account => !account.IsProfilePictureBackfillComplete ||
                                  account.ProfilePictureFileId is { } fileId &&
                                  _profilePictureFileService.GetProfilePicturePath(fileId) == null)
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
            Volatile.Write(ref _isRunning, 0);
        }
    }
}
