#nullable enable

using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Folders;

/// <summary>A folder in the navigation layout with the folders listed under it, in menu order.</summary>
public sealed record FolderLayoutNode(MailItemFolder Folder, List<FolderLayoutNode> Children);

/// <summary>
/// The visible folders of one account, placed the way the navigation pane lists them. Each folder appears
/// once: at the root, under its parent, or under one of the virtual Categories and More folders.
/// </summary>
public sealed class AccountFolderLayout
{
    public required MailAccount Account { get; init; }

    /// <summary>Folders listed directly under the account, in menu order.</summary>
    public required List<FolderLayoutNode> RootFolders { get; init; }

    /// <summary>The account's favorite categories, listed after the root folders.</summary>
    public required List<MailCategory> FavoriteCategories { get; init; }

    /// <summary>The folders a favorite category reads its mail from.</summary>
    public required List<IMailItemFolder> CategoryFolders { get; init; }

    /// <summary>The virtual Gmail Categories folder. Null when the account has nothing to list under it.</summary>
    public FolderLayoutNode? CategoriesFolder { get; init; }

    /// <summary>The virtual More folder. Null when the account has nothing to list under it.</summary>
    public FolderLayoutNode? MoreFolder { get; init; }

    public static AccountFolderLayout Empty(MailAccount account) => new()
    {
        Account = account,
        RootFolders = [],
        FavoriteCategories = [],
        CategoryFolders = []
    };
}

/// <summary>The folders an account group shows when its accounts' folders are combined.</summary>
public sealed class MergedAccountFolderLayout
{
    /// <summary>One entry per special folder type every account has; each lists that folder of every account.</summary>
    public required List<List<IMailItemFolder>> CommonFolders { get; init; }

    /// <summary>Favorite categories of the accounts, grouped by name and in display order.</summary>
    public required List<List<MailCategory>> FavoriteCategoryGroups { get; init; }

    /// <summary>The folders a favorite category reads its mail from.</summary>
    public required List<IMailItemFolder> CategoryFolders { get; init; }
}
