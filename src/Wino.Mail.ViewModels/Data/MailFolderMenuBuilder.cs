using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Mail.ViewModels.Data;

/// <summary>Builds the Mail pane's folder entries for an account or an account group from the folder layout the folder service returns.</summary>
public static class MailFolderMenuBuilder
{
    public static async Task<List<IMenuItem>> BuildAsync(IFolderService folderService, IAccountMenuItem accountMenuItem)
    {
        if (accountMenuItem is IMergedAccountMenuItem mergedAccountMenuItem)
        {
            var mergedLayout = await folderService
                .GetMergedAccountFolderLayoutAsync(mergedAccountMenuItem.HoldingAccounts)
                .ConfigureAwait(false);

            return CreateMergedAccountFolders(mergedLayout, mergedAccountMenuItem.Parameter);
        }

        var account = accountMenuItem.HoldingAccounts?.FirstOrDefault();

        if (account == null)
            return [];

        var layout = await folderService.GetAccountFolderLayoutAsync(account).ConfigureAwait(false);

        return CreateAccountFolders(layout, accountMenuItem);
    }

    public static List<IMenuItem> CreateAccountFolders(AccountFolderLayout layout, IMenuItem accountMenuItem)
    {
        var account = layout.Account;
        var items = new List<IMenuItem>();

        foreach (var node in layout.RootFolders)
        {
            items.Add(CreateFolderMenuItem(node, account, accountMenuItem));
        }

        foreach (var category in layout.FavoriteCategories)
        {
            items.Add(new MailCategoryMenuItem(category, account, layout.CategoryFolders, accountMenuItem));
        }

        if (layout.CategoriesFolder != null)
            items.Add(CreateFolderMenuItem(layout.CategoriesFolder, account, accountMenuItem));

        if (layout.MoreFolder != null)
            items.Add(CreateFolderMenuItem(layout.MoreFolder, account, accountMenuItem));

        return items;
    }

    public static List<IMenuItem> CreateMergedAccountFolders(MergedAccountFolderLayout layout, MergedInbox mergedInbox)
    {
        var items = new List<IMenuItem>();

        foreach (var folders in layout.CommonFolders)
        {
            if (folders.Count == 0)
                continue;

            items.Add(new MergedAccountFolderMenuItem(folders.ToList(), null, mergedInbox));
        }

        foreach (var categories in layout.FavoriteCategoryGroups)
        {
            items.Add(new MergedMailCategoryMenuItem(categories.ToList(), layout.CategoryFolders, mergedInbox));
        }

        return items;
    }

    private static FolderMenuItem CreateFolderMenuItem(FolderLayoutNode node, MailAccount account, IMenuItem parentMenuItem)
    {
        var folderMenuItem = new FolderMenuItem(node.Folder, account, parentMenuItem);

        foreach (var child in node.Children)
        {
            folderMenuItem.SubMenuItems.Add(CreateFolderMenuItem(child, account, folderMenuItem));
        }

        return folderMenuItem;
    }
}
