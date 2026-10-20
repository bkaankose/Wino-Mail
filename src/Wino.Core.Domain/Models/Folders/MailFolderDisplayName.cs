using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Folders;

/// <summary>
/// Localizes known folder roles without changing server names or custom folders.
/// </summary>
public static class MailFolderDisplayName
{
    public static string Get(IMailItemFolder folder) => folder.SpecialFolderType switch
    {
        SpecialFolderType.Inbox => Translator.MergedAccountCommonFolderInbox,
        SpecialFolderType.Draft => Translator.MergedAccountCommonFolderDraft,
        SpecialFolderType.Sent => Translator.MergedAccountCommonFolderSent,
        SpecialFolderType.Deleted => Translator.MergedAccountCommonFolderTrash,
        SpecialFolderType.Junk => Translator.MergedAccountCommonFolderJunk,
        SpecialFolderType.Archive => Translator.MergedAccountCommonFolderArchive,
        SpecialFolderType.More => Translator.MoreFolderNameOverride,
        SpecialFolderType.Category => Translator.CategoriesFolderNameOverride,
        SpecialFolderType.Starred => Translator.FolderDisplayName_Starred,
        SpecialFolderType.Important => Translator.FolderDisplayName_Important,
        SpecialFolderType.Chat => Translator.FolderDisplayName_Chat,
        SpecialFolderType.Unread => Translator.FolderDisplayName_Unread,
        SpecialFolderType.Forums => Translator.FolderDisplayName_Forums,
        SpecialFolderType.Updates => Translator.FolderDisplayName_Updates,
        SpecialFolderType.Personal => Translator.FolderDisplayName_Personal,
        SpecialFolderType.Promotions => Translator.FolderDisplayName_Promotions,
        SpecialFolderType.Social => Translator.FolderDisplayName_Social,
        _ => folder.FolderName,
    };
}
