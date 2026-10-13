#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Presentation;

/// <summary>Titles and WinoIcons glyphs for mail operations in menus, toolbars and swipe actions.</summary>
public static class MailOperationDisplay
{
    /// <summary>The translated command title; empty for operations without a visible title.</summary>
    public static string GetTitle(MailOperation operation) => operation switch
    {
        MailOperation.Archive => Translator.MailOperation_Archive,
        MailOperation.UnArchive => Translator.MailOperation_Unarchive,
        MailOperation.SoftDelete or MailOperation.HardDelete => Translator.MailOperation_Delete,
        MailOperation.Move => Translator.MailOperation_Move,
        MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
        MailOperation.MoveToFocused => Translator.MailOperation_MoveFocused,
        MailOperation.MoveToOther => Translator.MailOperation_MoveOther,
        MailOperation.AlwaysMoveToOther => Translator.MailOperation_AlwaysMoveOther,
        MailOperation.AlwaysMoveToFocused => Translator.MailOperation_AlwaysMoveFocused,
        MailOperation.SetFlag => Translator.MailOperation_SetFlag,
        MailOperation.ClearFlag => Translator.MailOperation_ClearFlag,
        MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
        MailOperation.MarkAsUnread => Translator.MailOperation_MarkAsUnread,
        MailOperation.MarkAsNotJunk => Translator.MailOperation_MarkNotJunk,
        MailOperation.Ignore => Translator.MailOperation_Ignore,
        MailOperation.Reply => Translator.MailOperation_Reply,
        MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
        MailOperation.Forward => Translator.MailOperation_Forward,
        MailOperation.Zoom => Translator.MailOperation_Zoom,
        MailOperation.SaveAs => Translator.MailOperation_SaveAs,
        MailOperation.SaveAsPdf => Translator.Buttons_PDF,
        MailOperation.SaveAsEml => Translator.Buttons_EML,
        MailOperation.Find => Translator.MailOperation_Find,
        MailOperation.Print => Translator.MailOperation_Print,
        MailOperation.ViewMessageSource => Translator.MailOperation_ViewMessageSource,
        MailOperation.RetryDraftUpload => Translator.Draft_RetryUpload,
        MailOperation.Navigate => Translator.MailOperation_Navigate,
        _ => string.Empty
    };

    /// <summary>The operation's glyph; <see cref="WinoIconGlyph.None"/> when it has none.</summary>
    public static WinoIconGlyph GetGlyph(MailOperation operation) => operation switch
    {
        MailOperation.Archive => WinoIconGlyph.Archive,
        MailOperation.UnArchive => WinoIconGlyph.UnArchive,
        MailOperation.SoftDelete or MailOperation.HardDelete => WinoIconGlyph.Delete,
        MailOperation.Move => WinoIconGlyph.Move,
        MailOperation.MoveToJunk or MailOperation.MarkAsNotJunk => WinoIconGlyph.Blocked,
        MailOperation.SetFlag => WinoIconGlyph.Flag,
        MailOperation.ClearFlag => WinoIconGlyph.ClearFlag,
        MailOperation.MarkAsRead => WinoIconGlyph.MarkRead,
        MailOperation.MarkAsUnread => WinoIconGlyph.MarkUnread,
        MailOperation.Ignore => WinoIconGlyph.Ignore,
        MailOperation.Reply => WinoIconGlyph.Reply,
        MailOperation.ReplyAll => WinoIconGlyph.ReplyAll,
        MailOperation.Forward => WinoIconGlyph.Forward,
        MailOperation.Zoom => WinoIconGlyph.Zoom,
        MailOperation.SaveAs or MailOperation.SaveAsPdf => WinoIconGlyph.Save,
        MailOperation.SaveAsEml or MailOperation.ViewMessageSource => WinoIconGlyph.ViewMessageSource,
        MailOperation.Print => WinoIconGlyph.Print,
        MailOperation.Find => WinoIconGlyph.Find,
        MailOperation.DarkEditor => WinoIconGlyph.DarkEditor,
        MailOperation.LightEditor => WinoIconGlyph.LightEditor,
        MailOperation.RetryDraftUpload => WinoIconGlyph.ArrowClockwise,
        MailOperation.MoveToFocused or MailOperation.AlwaysMoveToFocused => WinoIconGlyph.Important,
        MailOperation.MoveToOther or MailOperation.AlwaysMoveToOther => WinoIconGlyph.Inbox,
        _ => WinoIconGlyph.None
    };
}

/// <summary>Titles, glyphs and the destructive flag for folder context actions.</summary>
public static class FolderOperationDisplay
{
    public static string GetTitle(FolderOperation operation) => operation switch
    {
        FolderOperation.Pin => Translator.FolderOperation_Pin,
        FolderOperation.Unpin => Translator.FolderOperation_Unpin,
        FolderOperation.MarkAllAsRead => Translator.FolderOperation_MarkAllAsRead,
        FolderOperation.DontSync => Translator.FolderOperation_DontSync,
        FolderOperation.Empty => Translator.FolderOperation_Empty,
        FolderOperation.Rename => Translator.FolderOperation_Rename,
        FolderOperation.Delete => Translator.FolderOperation_Delete,
        FolderOperation.Move => Translator.FolderOperation_Move,
        FolderOperation.CreateSubFolder => Translator.FolderOperation_CreateSubFolder,
        _ => string.Empty
    };

    public static WinoIconGlyph GetGlyph(FolderOperation operation) => operation switch
    {
        FolderOperation.Pin => WinoIconGlyph.Pin,
        FolderOperation.Unpin => WinoIconGlyph.UnPin,
        FolderOperation.MarkAllAsRead => WinoIconGlyph.MarkRead,
        FolderOperation.DontSync => WinoIconGlyph.DontSync,
        FolderOperation.Empty => WinoIconGlyph.EmptyFolder,
        FolderOperation.Rename => WinoIconGlyph.Rename,
        FolderOperation.Delete => WinoIconGlyph.Delete,
        FolderOperation.Move => WinoIconGlyph.Forward,
        FolderOperation.TurnOffNotifications => WinoIconGlyph.TurnOfNotifications,
        FolderOperation.CreateSubFolder => WinoIconGlyph.CreateFolder,
        _ => WinoIconGlyph.None
    };

    /// <summary>Delete and Empty remove mail, so both platforms mark them destructive.</summary>
    public static bool IsDestructive(FolderOperation operation) => operation is FolderOperation.Delete or FolderOperation.Empty;

    /// <summary>Actions that open a dialog for more input before they run.</summary>
    public static bool AsksForInput(FolderOperation operation)
        => operation is FolderOperation.Rename or FolderOperation.Move or FolderOperation.CreateSubFolder;
}

/// <summary>Glyphs for special folders in the folder tree, pickers and filter editors.</summary>
public static class SpecialFolderGlyphs
{
    /// <summary>The special folder's glyph; <see cref="WinoIconGlyph.None"/> for regular folders.</summary>
    public static WinoIconGlyph GetGlyph(SpecialFolderType type) => type switch
    {
        SpecialFolderType.Inbox => WinoIconGlyph.SpecialFolderInbox,
        SpecialFolderType.Starred => WinoIconGlyph.SpecialFolderStarred,
        SpecialFolderType.Important => WinoIconGlyph.SpecialFolderImportant,
        SpecialFolderType.Sent => WinoIconGlyph.SpecialFolderSent,
        SpecialFolderType.Draft => WinoIconGlyph.SpecialFolderDraft,
        SpecialFolderType.Archive => WinoIconGlyph.SpecialFolderArchive,
        SpecialFolderType.Deleted => WinoIconGlyph.SpecialFolderDeleted,
        SpecialFolderType.Junk => WinoIconGlyph.SpecialFolderJunk,
        SpecialFolderType.Chat => WinoIconGlyph.SpecialFolderChat,
        SpecialFolderType.Category => WinoIconGlyph.SpecialFolderCategory,
        SpecialFolderType.Unread => WinoIconGlyph.SpecialFolderUnread,
        SpecialFolderType.Forums => WinoIconGlyph.SpecialFolderForums,
        SpecialFolderType.Updates => WinoIconGlyph.SpecialFolderUpdated,
        SpecialFolderType.Personal => WinoIconGlyph.SpecialFolderPersonal,
        SpecialFolderType.Promotions => WinoIconGlyph.SpecialFolderPromotions,
        SpecialFolderType.Social => WinoIconGlyph.SpecialFolderSocial,
        SpecialFolderType.Other => WinoIconGlyph.SpecialFolderOther,
        SpecialFolderType.More => WinoIconGlyph.SpecialFolderMore,
        _ => WinoIconGlyph.None
    };

    /// <summary>The special folder's glyph, or the plain folder glyph for regular folders.</summary>
    public static WinoIconGlyph GetGlyphOrFolder(SpecialFolderType type)
        => GetGlyph(type) is var glyph && glyph != WinoIconGlyph.None ? glyph : WinoIconGlyph.Folder;
}

/// <summary>Brand glyphs for mail providers.</summary>
public static class MailProviderGlyphs
{
    /// <summary>
    /// The provider's brand glyph. Catalog IMAP providers have a glyph named after their enum member
    /// (Gmx, Proton, ...); unknown ones use the IMAP glyph. <see cref="WinoIconGlyph.None"/> for an
    /// unknown provider type.
    /// </summary>
    public static WinoIconGlyph GetGlyph(MailProviderType providerType, SpecialImapProvider specialImapProvider)
    {
        if (specialImapProvider == SpecialImapProvider.None)
        {
            return providerType switch
            {
                MailProviderType.Outlook => WinoIconGlyph.Microsoft,
                MailProviderType.Gmail => WinoIconGlyph.Google,
                MailProviderType.IMAP4 => WinoIconGlyph.IMAP,
                _ => WinoIconGlyph.None,
            };
        }

        return specialImapProvider switch
        {
            SpecialImapProvider.iCloud => WinoIconGlyph.Apple,
            SpecialImapProvider.Yahoo => WinoIconGlyph.Yahoo,
            _ => System.Enum.TryParse<WinoIconGlyph>(specialImapProvider.ToString(), out var brandGlyph)
                ? brandGlyph
                : WinoIconGlyph.IMAP,
        };
    }
}
