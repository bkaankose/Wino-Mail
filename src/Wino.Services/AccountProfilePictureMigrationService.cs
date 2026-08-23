using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class AccountProfilePictureMigrationService
{
    private readonly IDatabaseService _databaseService;
    private readonly IAccountProfilePictureFileService _profilePictureFileService;
    private readonly IMessenger _messenger;
    private readonly ILogger _logger = Log.ForContext<AccountProfilePictureMigrationService>();

    public AccountProfilePictureMigrationService(
        IDatabaseService databaseService,
        IAccountProfilePictureFileService profilePictureFileService,
        IMessenger messenger)
    {
        _databaseService = databaseService;
        _profilePictureFileService = profilePictureFileService;
        _messenger = messenger;
    }

    public async Task RunAsync()
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
                    account.ProfilePictureFileId = await _profilePictureFileService
                        .SaveProfilePictureAsync(legacyBytes)
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
}
