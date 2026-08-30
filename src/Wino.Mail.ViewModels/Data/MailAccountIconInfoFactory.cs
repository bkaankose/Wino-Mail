using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Projects application account data into the host-independent account icon contract.
/// </summary>
public static class MailAccountIconInfoFactory
{
    public static AccountIconInfo Create(
        MailAccount account,
        IAccountProfilePictureFileService profilePictureFileService)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(profilePictureFileService);

        var profilePicturePath = account.ProfilePictureFileId is { } fileId
            ? profilePictureFileService.GetProfilePicturePath(fileId)
            : null;

        return new AccountIconInfo(
            MapProvider(account.ProviderType, account.SpecialImapProvider),
            profilePicturePath,
            account.AccountColorHex);
    }

    public static AccountIconInfo CreateProviderFallback(
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider) => new(
            MapProvider(providerType, specialImapProvider));

    private static AccountIconProvider MapProvider(
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider) => specialImapProvider switch
    {
        SpecialImapProvider.iCloud => AccountIconProvider.ICloud,
        SpecialImapProvider.Yahoo => AccountIconProvider.Yahoo,
        _ => providerType switch
        {
            MailProviderType.Outlook => AccountIconProvider.Microsoft,
            MailProviderType.Gmail => AccountIconProvider.Google,
            MailProviderType.IMAP4 => AccountIconProvider.Imap,
            MailProviderType.POP3 => AccountIconProvider.Imap,
            _ => AccountIconProvider.Imap,
        },
    };
}
