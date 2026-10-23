using System;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.Controls.Core.AccountIcon;

namespace MailCal.Mail.ViewModels.Data;

/// <summary>
/// Projects application account data into the host-independent account icon contract.
/// </summary>
public static class MailAccountIconInfoFactory
{
    public static AccountIconInfo Create(
        MailAccount account,
        IPictureStorageService profilePictureFileService)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(profilePictureFileService);

        var profilePicturePath = account.ProfilePictureFileId is { } fileId
            ? profilePictureFileService.GetPicturePath(PictureKind.AccountProfile, fileId)
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
