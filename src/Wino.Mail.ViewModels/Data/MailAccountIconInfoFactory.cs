#nullable enable
using System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Core.AccountIcon;

namespace Wino.Mail.ViewModels.Data;

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
            account.IsLocalOnly ? AccountIconProvider.Local : MapProvider(account.ProviderType, account.SpecialImapProvider),
            profilePicturePath,
            account.AccountColorHex,
            GetCatalogProviderGlyph(account.ProviderType, account.SpecialImapProvider));
    }

    public static AccountIconInfo CreateProviderFallback(
        MailProviderType providerType,
        SpecialImapProvider specialImapProvider) => new(
            MapProvider(providerType, specialImapProvider),
            ProviderGlyph: GetCatalogProviderGlyph(providerType, specialImapProvider));

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
            MailProviderType.Exchange => AccountIconProvider.Exchange,
            MailProviderType.IMAP4 => AccountIconProvider.Imap,
            MailProviderType.POP3 => AccountIconProvider.Imap,
            _ => AccountIconProvider.Imap,
        },
    };

    /// <summary>
    /// The brand mark of a catalog IMAP provider (Yandex, GMX, Proton, ...) that <see cref="AccountIconProvider"/> has no
    /// member for. Null when the provider maps to one of its members or has no mark of its own.
    /// </summary>
    private static string? GetCatalogProviderGlyph(MailProviderType providerType, SpecialImapProvider specialImapProvider)
    {
        if (specialImapProvider is SpecialImapProvider.None or SpecialImapProvider.iCloud or SpecialImapProvider.Yahoo)
            return null;

        var glyph = MailProviderGlyphs.GetGlyph(providerType, specialImapProvider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? null : WinoIconGlyphs.GetGlyph(glyph);
    }
}
