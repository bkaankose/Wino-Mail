using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Messaging.UI;

namespace MailCal.Services;

/// <summary>
/// Maps the addresses of the accounts in the app to their locally stored profile pictures,
/// so a mail sent by one of the user's own accounts shows that account's picture instead of
/// initials. Lookups are synchronous and free of IO for use from XAML bindings;
/// the map follows account create, update and remove messages.
/// </summary>
public sealed class AccountSenderPictureDirectory :
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountUpdatedMessage>,
    IRecipient<AccountRemovedMessage>
{
    private readonly IAccountService _accountService;
    private readonly IPictureStorageService _pictureStorage;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, (string Address, string PicturePath)> _entriesByAccountId = [];
    private IReadOnlyDictionary<string, string> _picturePathsByAddress =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AccountSenderPictureDirectory(
        IAccountService accountService,
        IPictureStorageService pictureStorage,
        IMessenger messenger)
    {
        _accountService = accountService;
        _pictureStorage = pictureStorage;

        messenger.Register<AccountCreatedMessage>(this);
        messenger.Register<AccountUpdatedMessage>(this);
        messenger.Register<AccountRemovedMessage>(this);
    }

    public async Task InitializeAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts)
            Update(account);
    }

    /// <summary>
    /// Returns the profile picture path of the account that owns <paramref name="address"/>,
    /// or null when no account owns it or the account has no stored picture.
    /// </summary>
    public string GetProfilePicturePath(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        return Volatile.Read(ref _picturePathsByAddress).TryGetValue(address.Trim(), out var path) ? path : null;
    }

    public void Receive(AccountCreatedMessage message) => Update(message.Account);

    public void Receive(AccountUpdatedMessage message) => Update(message.Account);

    public void Receive(AccountRemovedMessage message)
    {
        if (message.Account is null)
            return;

        lock (_lock)
        {
            if (_entriesByAccountId.Remove(message.Account.Id))
                Publish();
        }
    }

    private void Update(MailAccount account)
    {
        if (account is null)
            return;

        var address = account.Address?.Trim();
        var picturePath = account.ProfilePictureFileId is { } fileId
            ? _pictureStorage.GetPicturePath(PictureKind.AccountProfile, fileId)
            : null;

        lock (_lock)
        {
            if (string.IsNullOrEmpty(address) || picturePath is null)
            {
                if (_entriesByAccountId.Remove(account.Id))
                    Publish();

                return;
            }

            _entriesByAccountId[account.Id] = (address, picturePath);
            Publish();
        }
    }

    // Callers hold _lock. Readers see an immutable snapshot and never take the lock.
    private void Publish()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (address, picturePath) in _entriesByAccountId.Values)
            map.TryAdd(address, picturePath);

        Volatile.Write(ref _picturePathsByAddress, map);
    }
}
