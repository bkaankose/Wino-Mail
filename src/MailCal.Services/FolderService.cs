using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.MenuItems;
using MailCal.Core.Domain.Models.Accounts;
using MailCal.Core.Domain.Models.Badges;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Core.Domain.Models.Synchronization;
using MailCal.Messaging.UI;
using MailCal.Services.Extensions;

namespace MailCal.Services;

public class FolderService : BaseDatabaseService, IFolderService
{
    private readonly IAccountService _accountService;
    private readonly IMailCategoryService _mailCategoryService;
    private readonly ILogger _logger = Log.ForContext<FolderService>();

    public FolderService(IDatabaseService databaseService,
                           IAccountService accountService,
                           IMailCategoryService mailCategoryService) : base(databaseService)
    {
        _accountService = accountService;
        _mailCategoryService = mailCategoryService;
    }

    public async Task ChangeStickyStatusAsync(Guid folderId, bool isSticky)
        => await Connection.ExecuteAsync("UPDATE MailItemFolder SET IsSticky = ? WHERE Id = ?", isSticky, folderId);

    public async Task ChangeFolderHiddenStatusAsync(Guid folderId, bool isHidden)
    {
        await Connection.ExecuteAsync("UPDATE MailItemFolder SET IsHidden = ? WHERE Id = ?", isHidden, folderId);

        var folder = await GetFolderAsync(folderId).ConfigureAwait(false);
        if (folder != null)
        {
            Messenger.Send(new AccountFolderConfigurationUpdated(folder.MailAccountId));
        }
    }

    public async Task UpdateFolderOrdersAsync(Guid accountId, IReadOnlyList<Guid> orderedFolderIds)
    {
        if (orderedFolderIds == null || orderedFolderIds.Count == 0) return;

        await Connection.RunInTransactionAsync(conn =>
        {
            for (int i = 0; i < orderedFolderIds.Count; i++)
            {
                conn.Execute("UPDATE MailItemFolder SET \"Order\" = ? WHERE Id = ? AND MailAccountId = ?",
                    i + 1, orderedFolderIds[i], accountId);
            }
        }).ConfigureAwait(false);

        Messenger.Send(new AccountFolderConfigurationUpdated(accountId));
    }

    public async Task ResetFolderCustomizationAsync(Guid accountId)
    {
        await Connection.RunInTransactionAsync(conn =>
        {
            conn.Execute("UPDATE MailItemFolder SET \"Order\" = 0, IsHidden = 0 WHERE MailAccountId = ?", accountId);

            // Restore system folder stickiness. Category-type folders are virtual stickies too.
            conn.Execute(
                "UPDATE MailItemFolder SET IsSticky = 1 WHERE MailAccountId = ? AND (IsSystemFolder = 1 OR SpecialFolderType = ?)",
                accountId, (int)SpecialFolderType.Category);

            // Drop imported layout that has not been applied yet, otherwise it would resurrect
            // the customization the user just asked to reset once the folder arrives.
            conn.Execute("DELETE FROM FolderConfigurationOverride WHERE MailAccountId = ?", accountId);
        }).ConfigureAwait(false);

        Messenger.Send(new AccountFolderConfigurationUpdated(accountId));
    }

    public async Task UpsertFolderConfigurationOverrideAsync(FolderConfigurationOverride configurationOverride)
    {
        if (configurationOverride == null || string.IsNullOrEmpty(configurationOverride.RemoteFolderId)) return;

        var existingOverride = await GetFolderConfigurationOverrideAsync(configurationOverride.MailAccountId, configurationOverride.RemoteFolderId)
            .ConfigureAwait(false);

        if (existingOverride == null)
        {
            configurationOverride.Id = Guid.NewGuid();

            await Connection.InsertAsync(configurationOverride, typeof(FolderConfigurationOverride)).ConfigureAwait(false);
        }
        else
        {
            configurationOverride.Id = existingOverride.Id;

            await Connection.UpdateAsync(configurationOverride, typeof(FolderConfigurationOverride)).ConfigureAwait(false);
        }
    }

    public Task<List<FolderConfigurationOverride>> GetFolderConfigurationOverridesAsync(Guid accountId)
        => Connection.Table<FolderConfigurationOverride>().Where(a => a.MailAccountId == accountId).ToListAsync();

    public Task ClearFolderConfigurationOverridesAsync(Guid accountId)
        => Connection.ExecuteAsync("DELETE FROM FolderConfigurationOverride WHERE MailAccountId = ?", accountId);

    private Task<FolderConfigurationOverride> GetFolderConfigurationOverrideAsync(Guid accountId, string remoteFolderId)
        => Connection.Table<FolderConfigurationOverride>()
                     .FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.RemoteFolderId == remoteFolderId);

    /// <summary>
    /// Applies a pending online account folder layout to a folder that has just arrived from a synchronizer,
    /// then deletes the override so it is never applied twice.
    /// </summary>
    private async Task ApplyPendingFolderConfigurationAsync(MailItemFolder folder)
    {
        if (string.IsNullOrEmpty(folder.RemoteFolderId)) return;

        var configurationOverride = await GetFolderConfigurationOverrideAsync(folder.MailAccountId, folder.RemoteFolderId).ConfigureAwait(false);

        if (configurationOverride == null) return;

        folder.IsSticky = configurationOverride.IsSticky;
        folder.IsHidden = configurationOverride.IsHidden;
        folder.Order = configurationOverride.Order;
        folder.ShowUnreadCount = configurationOverride.ShowUnreadCount;
        folder.IsCountedInAccountTotal = configurationOverride.IsCountedInAccountTotal;
        folder.IsJumpListEnabled = configurationOverride.IsJumpListEnabled;

        await Connection.DeleteAsync<FolderConfigurationOverride>(configurationOverride.Id).ConfigureAwait(false);

        _logger.Debug("Applied imported folder configuration for {RemoteFolderId} on account {MailAccountId}.",
            folder.RemoteFolderId, folder.MailAccountId);
    }

    private static int GetDefaultFolderOrder(MailItemFolder folder)
        => folder.SpecialFolderType == SpecialFolderType.Other
            ? int.MaxValue
            : (int)folder.SpecialFolderType;

    /// <summary>
    /// Orders folders by user-set Order first (customized entries ahead of uncustomized ones),
    /// then falls back to SpecialFolderType enum order for known special folders so defaults
    /// like Inbox stay at the top, and finally to alphabetic folder name (culture-aware).
    /// </summary>
    private static IOrderedEnumerable<MailItemFolder> ApplyFolderSort(IEnumerable<MailItemFolder> folders)
        => folders
            .OrderBy(a => a.Order == 0 ? 1 : 0)
            .ThenBy(a => a.Order)
            .ThenBy(GetDefaultFolderOrder)
            .ThenBy(a => a.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.SpecialFolderType);

    public async Task<int> GetFolderUnreadCountAsync(Guid folderId)
    {
        var folder = await GetFolderAsync(folderId);

        if (folder == null) return default;

        var account = await _accountService.GetAccountAsync(folder.MailAccountId);

        if (account == null) return default;

        return await GetFolderUnreadCountAsync(folder, account).ConfigureAwait(false);
    }

    public async Task<int> GetFolderUnreadCountAsync(Guid folderId, bool isFocused)
    {
        var folder = await GetFolderAsync(folderId).ConfigureAwait(false);

        if (folder == null)
            return 0;

        return await Connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM MailCopy WHERE FolderId = ? AND IsFocused = ? AND IsRead = ?",
            folder.Id,
            isFocused ? 1 : 0,
            0).ConfigureAwait(false);
    }

    public async Task<List<UnreadBadgeFolderContribution>> GetCountedFolderUnreadCountsAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId);

        if (account == null) return [];

        // A hidden folder is not offered on the badge page, so it cannot feed the total either.
        var folders = await GetVisibleFoldersAsync(accountId).ConfigureAwait(false);
        var countedFolders = GetCountedFolders(folders, account.Preferences.UnreadBadgeCountSource);

        var contributions = new List<UnreadBadgeFolderContribution>();

        foreach (var folder in countedFolders)
        {
            var unreadCount = await GetFolderUnreadCountAsync(folder, account).ConfigureAwait(false);

            contributions.Add(new UnreadBadgeFolderContribution(folder.Id, folder.FolderName, unreadCount));
        }

        return contributions;
    }

    /// <summary>
    /// Inbox only is the default and does not depend on any per-folder flag, so an account that never
    /// visited the badge settings keeps counting exactly what it counted before.
    /// </summary>
    private static List<MailItemFolder> GetCountedFolders(List<MailItemFolder> folders, UnreadBadgeCountSource countSource)
        => countSource == UnreadBadgeCountSource.SelectedFolders
            ? folders.Where(folder => folder.IsCountedInAccountTotal && folder.IsMoveTarget).ToList()
            : folders.Where(folder => folder.SpecialFolderType == SpecialFolderType.Inbox).ToList();

    private async Task<int> GetFolderUnreadCountAsync(MailItemFolder folder, MailAccount account)
    {
        var folderId = folder.Id;

        // Convert to raw SQL
        string sqlQuery;
        object[] parameters;
        
        if (account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault() && folder.SpecialFolderType == SpecialFolderType.Inbox)
        {
            if (folder.SpecialFolderType != SpecialFolderType.Draft && folder.SpecialFolderType != SpecialFolderType.Junk)
            {
                sqlQuery = "SELECT COUNT(*) FROM MailCopy WHERE FolderId = ? AND IsFocused = ? AND IsRead = ?";
                parameters = new object[] { folderId, 1, 0 };
            }
            else
            {
                sqlQuery = "SELECT COUNT(*) FROM MailCopy WHERE FolderId = ? AND IsFocused = ?";
                parameters = new object[] { folderId, 1 };
            }
        }
        else
        {
            if (folder.SpecialFolderType != SpecialFolderType.Draft && folder.SpecialFolderType != SpecialFolderType.Junk)
            {
                sqlQuery = "SELECT COUNT(*) FROM MailCopy WHERE FolderId = ? AND IsRead = ?";
                parameters = new object[] { folderId, 0 };
            }
            else
            {
                sqlQuery = "SELECT COUNT(*) FROM MailCopy WHERE FolderId = ?";
                parameters = new object[] { folderId };
            }
        }

        return await Connection.ExecuteScalarAsync<int>(sqlQuery, parameters);
    }

    public async Task<AccountFolderTree> GetFolderStructureForAccountAsync(Guid accountId, bool includeHiddenFolders)
    {
        var account = await _accountService.GetAccountAsync(accountId);

        if (account == null)
            throw new ArgumentException(nameof(account));

        var accountTree = new AccountFolderTree(account);

        var allFolders = await Connection
            .Table<MailItemFolder>()
            .Where(folder => folder.MailAccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);

        var folders = includeHiddenFolders ? allFolders : FilterVisibleFolders(allFolders);
        var hierarchy = FolderHierarchy.Build(folders, pinnedFoldersAreRoots: false, accountId, _logger);

        foreach (var folder in folders)
        {
            folder.ChildFolders = hierarchy.GetChildren(folder).Cast<IMailItemFolder>().ToList();
        }

        foreach (var rootFolder in hierarchy.Roots)
        {
            accountTree.Folders.Add(rootFolder);
        }

        return accountTree;
    }

    /// <summary>
    /// A folder is visible when it is not hidden and its parent is visible, so hiding a folder hides
    /// its whole subtree. Pinned folders are the exception: they stay visible under a hidden parent.
    /// A parent that is missing, the folder itself, or part of a cycle does not hide anything.
    /// </summary>
    internal static List<MailItemFolder> FilterVisibleFolders(IReadOnlyCollection<MailItemFolder> folders)
    {
        var foldersByRemoteId = GetFoldersByRemoteId(folders);
        var visibility = new Dictionary<MailItemFolder, bool>(ReferenceEqualityComparer.Instance);
        var resolving = new HashSet<MailItemFolder>(ReferenceEqualityComparer.Instance);

        bool IsVisible(MailItemFolder folder)
        {
            if (visibility.TryGetValue(folder, out var isKnownVisible))
                return isKnownVisible;

            if (folder.IsHidden || folder.IsSticky)
                return visibility[folder] = !folder.IsHidden;

            // Revisiting a folder means the parent links form a cycle. The hierarchy promotes it to a root.
            if (!resolving.Add(folder))
                return true;

            var isVisible = !TryGetParentFolder(folder, foldersByRemoteId, out var parentFolder) || IsVisible(parentFolder);

            resolving.Remove(folder);

            return visibility[folder] = isVisible;
        }

        return folders.Where(IsVisible).ToList();
    }

    private static Dictionary<string, MailItemFolder> GetFoldersByRemoteId(IEnumerable<MailItemFolder> folders)
        => folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .GroupBy(folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static bool TryGetParentFolder(MailItemFolder folder,
                                           IReadOnlyDictionary<string, MailItemFolder> foldersByRemoteId,
                                           out MailItemFolder parentFolder)
    {
        parentFolder = null;

        if (string.IsNullOrWhiteSpace(folder.ParentRemoteFolderId) ||
            !foldersByRemoteId.TryGetValue(folder.ParentRemoteFolderId, out var candidate) ||
            ReferenceEquals(candidate, folder))
        {
            return false;
        }

        parentFolder = candidate;
        return true;
    }

    /// <summary>
    /// Gives every folder at most one parent so each folder is listed exactly once. Providers report
    /// parents by remote id; a parent that is not in the set, the folder itself, or a cycle makes the
    /// folder a root instead of losing or repeating it. Roots and every child list are in menu order.
    /// </summary>
    private sealed class FolderHierarchy
    {
        private readonly Dictionary<MailItemFolder, List<MailItemFolder>> _childrenByFolder;

        private FolderHierarchy(List<MailItemFolder> folders, List<MailItemFolder> roots, Dictionary<MailItemFolder, List<MailItemFolder>> childrenByFolder)
        {
            Folders = folders;
            Roots = roots;
            _childrenByFolder = childrenByFolder;
        }

        /// <summary>Every folder in the hierarchy, in menu order.</summary>
        public IReadOnlyList<MailItemFolder> Folders { get; }

        public IReadOnlyList<MailItemFolder> Roots { get; }

        public IReadOnlyList<MailItemFolder> GetChildren(MailItemFolder folder)
            => _childrenByFolder.TryGetValue(folder, out var children) ? children : Array.Empty<MailItemFolder>();

        /// <param name="pinnedFoldersAreRoots">
        /// The navigation menu lists pinned folders directly under the account, so they are not
        /// repeated under their parent there.
        /// </param>
        public static FolderHierarchy Build(IReadOnlyCollection<MailItemFolder> folders, bool pinnedFoldersAreRoots, Guid accountId, ILogger logger)
        {
            var duplicateRemoteIds = folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(folder => folder.RemoteFolderId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);

            foreach (var remoteId in duplicateRemoteIds)
            {
                logger.Warning(
                    "Duplicate folder remote id {RemoteFolderId} found while building folder hierarchy for account {AccountId}.",
                    remoteId,
                    accountId);
            }

            var foldersByRemoteId = GetFoldersByRemoteId(folders);
            var parentByFolder = new Dictionary<MailItemFolder, MailItemFolder>(ReferenceEqualityComparer.Instance);

            foreach (var folder in folders)
            {
                if (pinnedFoldersAreRoots && folder.IsSticky)
                    continue;

                if (TryGetParentFolder(folder, foldersByRemoteId, out var parentFolder))
                    parentByFolder[folder] = parentFolder;
            }

            foreach (var folder in folders)
            {
                if (!CreatesFolderCycle(folder, parentByFolder))
                    continue;

                parentByFolder.Remove(folder);
                logger.Warning(
                    "Folder hierarchy cycle detected at {RemoteFolderId} in account {AccountId}. Promoting it to a root folder.",
                    folder.RemoteFolderId,
                    accountId);
            }

            var childrenByFolder = new Dictionary<MailItemFolder, List<MailItemFolder>>(ReferenceEqualityComparer.Instance);

            foreach (var (folder, parentFolder) in parentByFolder)
            {
                if (!childrenByFolder.TryGetValue(parentFolder, out var children))
                    childrenByFolder[parentFolder] = children = [];

                children.Add(folder);
            }

            foreach (var parentFolder in childrenByFolder.Keys.ToList())
            {
                childrenByFolder[parentFolder] = ApplyFolderSort(childrenByFolder[parentFolder]).ToList();
            }

            var roots = ApplyFolderSort(folders.Where(folder => !parentByFolder.ContainsKey(folder))).ToList();

            return new FolderHierarchy(ApplyFolderSort(folders).ToList(), roots, childrenByFolder);
        }

        private static bool CreatesFolderCycle(MailItemFolder folder, IReadOnlyDictionary<MailItemFolder, MailItemFolder> parentByFolder)
        {
            var visited = new HashSet<MailItemFolder>(ReferenceEqualityComparer.Instance) { folder };
            var currentFolder = folder;

            while (parentByFolder.TryGetValue(currentFolder, out var parentFolder))
            {
                if (!visited.Add(parentFolder))
                    return true;

                currentFolder = parentFolder;
            }

            return false;
        }
    }

    /// <summary>
    /// Where a root of the display hierarchy sits in the navigation menu. The move menu mirrors the
    /// same shape, so this rule has to stay in one place. Folders with a visible parent are reached
    /// through that parent and never get a placement of their own.
    /// </summary>
    private enum FolderDisplayPlacement
    {
        /// <summary>Listed directly under the account.</summary>
        Root,

        /// <summary>Listed under the virtual Categories folder.</summary>
        Categories,

        /// <summary>Listed under the virtual More folder.</summary>
        More,

        /// <summary>Not listed at this level.</summary>
        Nested
    }

    private static FolderDisplayPlacement GetFolderDisplayPlacement(MailItemFolder folder)
    {
        // Category type folders should be skipped. They will be categorized under virtual category folder.
        if (ServiceConstants.SubCategoryFolderLabelIds.Contains(folder.RemoteFolderId))
            return FolderDisplayPlacement.Nested;

        if (folder.IsSticky)
            return FolderDisplayPlacement.Root;

        return ServiceConstants.SubCategoryFolderLabelIds.Contains(folder.FolderName?.ToUpper())
            ? FolderDisplayPlacement.Categories
            : FolderDisplayPlacement.More;
    }

    private async Task<FolderHierarchy> GetDisplayHierarchyAsync(Guid accountId)
    {
        var folders = await GetVisibleFoldersAsync(accountId).ConfigureAwait(false);

        return FolderHierarchy.Build(folders, pinnedFoldersAreRoots: true, accountId, _logger);
    }

    private static void LocalizeCategoryFolderName(MailItemFolder folder)
    {
        if (folder.SpecialFolderType == SpecialFolderType.Category) folder.FolderName = Translator.CategoriesFolderNameOverride;
    }

    public async Task<List<IMailItemFolder>> GetFolderStructureForDisplayAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId);

        if (account == null)
            throw new ArgumentException(nameof(account));

        var hierarchy = await GetDisplayHierarchyAsync(accountId).ConfigureAwait(false);

        if (hierarchy.Roots.Count == 0) return [];

        var rootFolders = new List<IMailItemFolder>();
        var moreFolder = MailItemFolder.CreateMoreFolder();
        var categoryFolder = MailItemFolder.CreateCategoriesFolder();

        foreach (var folder in hierarchy.Roots)
        {
            var placement = GetFolderDisplayPlacement(folder);

            if (placement == FolderDisplayPlacement.Nested) continue;

            AttachChildFolders(hierarchy, folder);

            switch (placement)
            {
                case FolderDisplayPlacement.Root:
                    rootFolders.Add(folder);
                    break;
                case FolderDisplayPlacement.Categories:
                    categoryFolder.ChildFolders.Add(folder);
                    break;
                default:
                    moreFolder.ChildFolders.Add(folder);
                    break;
            }
        }

        // An empty virtual folder would be a dead end here, unlike the navigation menu where
        // Categories is always offered for Gmail.
        if (categoryFolder.ChildFolders.Count > 0) rootFolders.Add(categoryFolder);

        if (moreFolder.ChildFolders.Count > 0) rootFolders.Add(moreFolder);

        return rootFolders;
    }

    private static void AttachChildFolders(FolderHierarchy hierarchy, MailItemFolder folder)
    {
        // Localize category folder name, the way the navigation menu does.
        LocalizeCategoryFolderName(folder);

        var childFolders = hierarchy.GetChildren(folder);

        folder.ChildFolders = childFolders.Cast<IMailItemFolder>().ToList();

        foreach (var childFolder in childFolders)
        {
            AttachChildFolders(hierarchy, childFolder);
        }
    }

    public Task<IEnumerable<IMenuItem>> GetAccountFoldersForDisplayAsync(IAccountMenuItem accountMenuItem)
    {
        if (accountMenuItem is IMergedAccountMenuItem mergedAccountFolderMenuItem)
        {
            return GetMergedAccountFolderMenuItemsAsync(mergedAccountFolderMenuItem);
        }
        else
        {
            return GetSingleAccountFolderMenuItemsAsync(accountMenuItem);
        }
    }

    private static FolderMenuItem CreateFolderMenuItem(FolderHierarchy hierarchy, MailAccount account, MailItemFolder folder, IMenuItem parentMenuItem)
    {
        // Localize category folder name.
        LocalizeCategoryFolderName(folder);

        var folderMenuItem = new FolderMenuItem(folder, account, parentMenuItem);

        foreach (var childFolder in hierarchy.GetChildren(folder))
        {
            folderMenuItem.SubMenuItems.Add(CreateFolderMenuItem(hierarchy, account, childFolder, folderMenuItem));
        }

        return folderMenuItem;
    }

    private async Task<IEnumerable<IMenuItem>> GetSingleAccountFolderMenuItemsAsync(IAccountMenuItem accountMenuItem)
    {
        var accountId = accountMenuItem.EntityId.Value;
        var preparedFolderMenuItems = new List<IMenuItem>();

        // Visible folders only. Each one is listed once: at its placement, or under its parent.
        var hierarchy = await GetDisplayHierarchyAsync(accountId).ConfigureAwait(false);

        if (hierarchy.Roots.Count == 0) return new List<IMenuItem>();

        var mailAccount = accountMenuItem.HoldingAccounts.First();

        var moreFolder = MailItemFolder.CreateMoreFolder();
        var categoryFolder = MailItemFolder.CreateCategoriesFolder();

        var moreFolderMenuItem = new FolderMenuItem(moreFolder, mailAccount, accountMenuItem);
        var categoryFolderMenuItem = new FolderMenuItem(categoryFolder, mailAccount, accountMenuItem);

        foreach (var item in hierarchy.Roots)
        {
            var placement = GetFolderDisplayPlacement(item);

            if (placement == FolderDisplayPlacement.Nested) continue;

            // Sticky items belong to account menu item directly. Rest goes to More folder.
            IMenuItem parentFolderMenuItem = placement switch
            {
                FolderDisplayPlacement.Root => accountMenuItem,
                FolderDisplayPlacement.Categories => categoryFolderMenuItem,
                _ => moreFolderMenuItem
            };

            var preparedItem = CreateFolderMenuItem(hierarchy, mailAccount, item, parentFolderMenuItem);

            // Don't add menu items that are prepared for More folder. They've been included in More virtual folder already.
            // We'll add More folder later on at the end of the list.

            if (placement == FolderDisplayPlacement.Root)
            {
                preparedFolderMenuItems.Add(preparedItem);
            }
            else if (parentFolderMenuItem is FolderMenuItem baseParentFolderMenuItem)
            {
                baseParentFolderMenuItem.SubMenuItems.Add(preparedItem);
            }
        }

        var favoriteCategories = await GetFavoriteCategoryMenuItemsAsync(mailAccount, hierarchy.Folders, accountMenuItem).ConfigureAwait(false);
        preparedFolderMenuItems.AddRange(favoriteCategories);

        // An empty Categories item is a dead end after the user hides every Gmail category.
        if (mailAccount.ProviderType == MailProviderType.Gmail && categoryFolderMenuItem.SubMenuItems.Any())
        {
            preparedFolderMenuItems.Add(categoryFolderMenuItem);
        }

        // Only add More folder if there are any items in it.
        if (moreFolderMenuItem.SubMenuItems.Any()) preparedFolderMenuItems.Add(moreFolderMenuItem);

        return preparedFolderMenuItems;
    }

    private async Task<IEnumerable<IMenuItem>> GetMergedAccountFolderMenuItemsAsync(IMergedAccountMenuItem mergedAccountFolderMenuItem)
    {
        var holdingAccounts = mergedAccountFolderMenuItem.HoldingAccounts;

        if (holdingAccounts == null || !holdingAccounts.Any()) return [];

        var preparedFolderMenuItems = new List<IMenuItem>();

        // First gather all account folders.
        // Prepare single menu items for both of them.

        var allAccountFolders = new List<List<MailItemFolder>>();

        foreach (var account in holdingAccounts)
        {
            var accountFolders = await GetVisibleFoldersAsync(account.Id).ConfigureAwait(false);

            allAccountFolders.Add(accountFolders);
        }

        var commonFolders = FindCommonFolders(allAccountFolders);

        // Prepare menu items for common folders.
        foreach (var commonFolderType in commonFolders)
        {
            var folderItems = allAccountFolders.SelectMany(a => a.Where(b => b.SpecialFolderType == commonFolderType)).Cast<IMailItemFolder>().ToList();
            var menuItem = new MergedAccountFolderMenuItem(folderItems, null, mergedAccountFolderMenuItem.Parameter);

            preparedFolderMenuItems.Add(menuItem);
        }

        var favoriteCategories = await GetMergedFavoriteCategoryMenuItemsAsync(holdingAccounts, allAccountFolders, mergedAccountFolderMenuItem.Parameter).ConfigureAwait(false);
        preparedFolderMenuItems.AddRange(favoriteCategories);

        return preparedFolderMenuItems;
    }

    private async Task<IEnumerable<IMenuItem>> GetFavoriteCategoryMenuItemsAsync(MailAccount account, IEnumerable<IMailItemFolder> handlingFolders, IMenuItem parentMenuItem)
    {
        var favoriteCategories = await _mailCategoryService.GetFavoriteCategoriesAsync(account.Id).ConfigureAwait(false);

        if (!favoriteCategories.Any())
            return [];

        var availableFolders = handlingFolders
            .Where(a => a.IsMoveTarget)
            .Cast<IMailItemFolder>()
            .ToList();

        return favoriteCategories
            .Select(category => (IMenuItem)new MailCategoryMenuItem(category, account, availableFolders, parentMenuItem))
            .ToList();
    }

    private async Task<IEnumerable<IMenuItem>> GetMergedFavoriteCategoryMenuItemsAsync(IEnumerable<MailAccount> holdingAccounts, IEnumerable<IEnumerable<MailItemFolder>> allAccountFolders, MergedInbox mergedInbox)
    {
        var categoriesByAccount = new List<(MailAccount Account, List<MailCategory> Categories)>();

        foreach (var account in holdingAccounts)
        {
            var categories = await _mailCategoryService.GetFavoriteCategoriesAsync(account.Id).ConfigureAwait(false);
            if (categories.Any())
            {
                categoriesByAccount.Add((account, categories));
            }
        }

        if (!categoriesByAccount.Any())
            return [];

        var handlingFolders = allAccountFolders
            .SelectMany(a => a)
            .Where(a => a.IsMoveTarget)
            .Cast<IMailItemFolder>()
            .ToList();

        return categoriesByAccount
            .SelectMany(a => a.Categories)
            .GroupBy(a => NormalizeCategoryName(a.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => (IMenuItem)new MergedMailCategoryMenuItem(group.ToList(), handlingFolders, mergedInbox))
            .OrderBy(item => ((MergedMailCategoryMenuItem)item).FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeCategoryName(string name)
        => name?.Trim() ?? string.Empty;

    private HashSet<SpecialFolderType> FindCommonFolders(List<List<MailItemFolder>> lists)
    {
        var allSpecialTypesExceptOther = Enum.GetValues<SpecialFolderType>().Cast<SpecialFolderType>().Where(a => a != SpecialFolderType.Other).ToList();

        // Start with all special folder types from the first list
        var commonSpecialFolderTypes = new HashSet<SpecialFolderType>(allSpecialTypesExceptOther);

        // Intersect with special folder types from all lists
        foreach (var list in lists)
        {
            commonSpecialFolderTypes.IntersectWith(list.Select(f => f.SpecialFolderType));
        }

        return commonSpecialFolderTypes;
    }

    public async Task<MailItemFolder> GetSpecialFolderByAccountIdAsync(Guid accountId, SpecialFolderType type)
        => await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.SpecialFolderType == type);

    public async Task<MailItemFolder> GetFolderAsync(Guid folderId)
        => await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.Id.Equals(folderId));

    public Task<int> GetCurrentItemCountForFolder(Guid folderId)
        => Connection.Table<MailCopy>().Where(a => a.FolderId == folderId).CountAsync();

    public async Task<List<MailItemFolder>> GetFoldersAsync(Guid accountId)
    {
        // Ordering is applied in managed code so that StringComparer.CurrentCultureIgnoreCase
        // is honored. SQLite's default ORDER BY is not culture-aware.
        const string query = "SELECT * FROM MailItemFolder WHERE MailAccountId = ?";
        var rows = await Connection.QueryAsync<MailItemFolder>(query, accountId).ConfigureAwait(false);
        return ApplyFolderSort(rows).ToList();
    }

    public async Task<List<MailItemFolder>> GetFoldersByIdsAsync(
        IReadOnlyCollection<Guid> folderIds,
        CancellationToken cancellationToken = default)
    {
        if (folderIds == null || folderIds.Count == 0)
            return [];

        var distinctIds = folderIds.Distinct().ToArray();
        var folders = new List<MailItemFolder>(distinctIds.Length);
        const int batchSize = 400;

        for (var offset = 0; offset < distinctIds.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = distinctIds.Skip(offset).Take(batchSize).ToArray();
            var placeholders = string.Join(",", batch.Select(_ => "?"));
            var query = $"SELECT * FROM MailItemFolder WHERE Id IN ({placeholders})";
            var rows = await Connection
                .QueryAsync<MailItemFolder>(query, batch.Cast<object>().ToArray())
                .ConfigureAwait(false);

            folders.AddRange(rows);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return folders;
    }

    public async Task<List<MailItemFolder>> GetVisibleFoldersAsync(Guid accountId)
    {
        // Visibility depends on the parent chain, so hidden rows are loaded too.
        const string query = "SELECT * FROM MailItemFolder WHERE MailAccountId = ?";
        var rows = await Connection.QueryAsync<MailItemFolder>(query, accountId).ConfigureAwait(false);
        return ApplyFolderSort(FilterVisibleFolders(rows)).ToList();
    }

    public async Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId)
    {
        var mailCopies = await Connection.QueryAsync<MailCopy>(
            "SELECT Id, ImapUid FROM MailCopy WHERE FolderId = ?",
            folderId).ConfigureAwait(false);

        var knownUids = new HashSet<uint>();

        foreach (var mailCopy in mailCopies)
        {
            if (mailCopy.ImapUid > 0)
            {
                knownUids.Add(mailCopy.ImapUid);
                continue;
            }

            if (MailkitClientExtensions.TryResolveUid(mailCopy.Id, out var parsedUid))
                knownUids.Add(parsedUid);
        }

        return knownUids.ToList();
    }

    public async Task<MailAccount> UpdateSystemFolderConfigurationAsync(Guid accountId, SystemFolderConfiguration configuration)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        // Update system folders for this account.

        await Task.WhenAll(UpdateSystemFolderInternalAsync(configuration.SentFolder, SpecialFolderType.Sent),
                           UpdateSystemFolderInternalAsync(configuration.DraftFolder, SpecialFolderType.Draft),
                           UpdateSystemFolderInternalAsync(configuration.JunkFolder, SpecialFolderType.Junk),
                           UpdateSystemFolderInternalAsync(configuration.TrashFolder, SpecialFolderType.Deleted),
                           UpdateSystemFolderInternalAsync(configuration.ArchiveFolder, SpecialFolderType.Archive));


        return await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
    }

    private Task UpdateSystemFolderInternalAsync(MailItemFolder folder, SpecialFolderType assignedSpecialFolderType)
    {
        if (folder == null) return Task.CompletedTask;

        folder.IsSticky = true;
        folder.IsSynchronizationEnabled = true;
        folder.IsSystemFolder = true;
        folder.SpecialFolderType = assignedSpecialFolderType;

        return UpdateFolderAsync(folder);
    }

    public async Task ChangeFolderSynchronizationStateAsync(Guid folderId, bool isSynchronizationEnabled)
    {
        var localFolder = await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.Id == folderId);

        if (localFolder != null)
        {
            localFolder.IsSynchronizationEnabled = isSynchronizationEnabled;

            await UpdateFolderAsync(localFolder).ConfigureAwait(false);

            Messenger.Send(new FolderSynchronizationEnabled(localFolder));
        }
    }

    #region Repository Calls

    public async Task InsertFolderAsync(MailItemFolder folder)
    {
        if (folder == null)
        {
            _logger.Warning("Folder is null. Cannot insert.");

            return;
        }

        var account = await _accountService.GetAccountAsync(folder.MailAccountId);

        if (account == null)
        {
            _logger.Warning("Account with id {MailAccountId} does not exist. Cannot insert folder.", folder.MailAccountId);

            return;
        }

        var existingFolder = await GetFolderAsync(folder.Id).ConfigureAwait(false);

        // IMAP servers don't have unique identifier for folders all the time.
        // So we'll try to match them with remote folder id and account id relation.
        // If we have a match, we'll update the folder instead of inserting.

        existingFolder ??= await GetFolderAsync(folder.MailAccountId, folder.RemoteFolderId).ConfigureAwait(false);

        if (existingFolder == null)
        {
            folder.IsJumpListEnabled = folder.SpecialFolderType == SpecialFolderType.Inbox;

            // A online account import may have parked a navigation layout for this folder before it existed.
            await ApplyPendingFolderConfigurationAsync(folder).ConfigureAwait(false);

            _logger.Debug("Inserting folder {Id} - {FolderName}", folder.Id, folder.FolderName, folder.MailAccountId);

            await Connection.InsertAsync(folder, typeof(MailItemFolder)).ConfigureAwait(false);
        }
        else
        {
            // TODO: This is not alright. We should've updated the folder instead of inserting.
            // Now we need to match the properties that user might've set locally.

            folder.Id = existingFolder.Id;
            folder.IsSticky = existingFolder.IsSticky;
            folder.SpecialFolderType = existingFolder.SpecialFolderType;
            folder.ShowUnreadCount = existingFolder.ShowUnreadCount;
            folder.IsCountedInAccountTotal = existingFolder.IsCountedInAccountTotal;
            folder.TextColorHex = existingFolder.TextColorHex;
            folder.BackgroundColorHex = existingFolder.BackgroundColorHex;
            folder.Order = existingFolder.Order;
            folder.IsHidden = existingFolder.IsHidden;
            folder.IsJumpListEnabled = existingFolder.IsJumpListEnabled;
            folder.LastSynchronizedDate = existingFolder.LastSynchronizedDate;
            folder.UidValidity = existingFolder.UidValidity;
            folder.HighestModeSeq = existingFolder.HighestModeSeq;
            folder.HighestKnownUid = existingFolder.HighestKnownUid;
            folder.LastUidReconcileUtc = existingFolder.LastUidReconcileUtc;
            folder.DeltaToken = existingFolder.DeltaToken;

            // An imported layout represents newer user intent than the values preserved above, so it wins.
            await ApplyPendingFolderConfigurationAsync(folder).ConfigureAwait(false);

            _logger.Debug("Folder {Id} - {FolderName} already exists. Updating.", folder.Id, folder.FolderName);

            await UpdateFolderAsync(folder).ConfigureAwait(false);
        }
    }

    public async Task UpdateFolderAsync(MailItemFolder folder)
    {
        if (folder == null)
        {
            _logger.Warning("Folder is null. Cannot update.");

            return;
        }

        _logger.Debug("Updating folder {FolderName}", folder.Id, folder.FolderName);

        await Connection.UpdateAsync(folder, typeof(MailItemFolder)).ConfigureAwait(false);
    }

    public Task UpdateFolderHighestModeSeqAsync(Guid folderId, long highestModeSeq)
        => Connection.ExecuteAsync("UPDATE MailItemFolder SET HighestModeSeq = ? WHERE Id = ?", highestModeSeq, folderId);

    private async Task DeleteFolderAsync(MailItemFolder folder)
    {
        if (folder == null)
        {
            _logger.Warning("Folder is null. Cannot delete.");

            return;
        }

        var account = await _accountService.GetAccountAsync(folder.MailAccountId).ConfigureAwait(false);
        if (account == null)
        {
            _logger.Warning("Account with id {MailAccountId} does not exist. Cannot delete folder.", folder.MailAccountId);
            return;
        }

        _logger.Debug("Deleting folder {FolderName}", folder.FolderName);

        await Connection.DeleteAsync<MailItemFolder>(folder.Id).ConfigureAwait(false);

        // Delete all existing mails from this folder.
        await Connection.ExecuteAsync("DELETE FROM MailCopy WHERE FolderId = ?", folder.Id);

        // TODO: Delete MIME messages from the disk.
    }

    #endregion

    private Task<List<string>> GetMailCopyIdsByFolderIdAsync(Guid folderId)
    {
        const string query = "SELECT Id FROM MailCopy WHERE FolderId = ?";
        return Connection.QueryScalarsAsync<string>(query, folderId);
    }

    public async Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(IEnumerable<string> mailCopyIds)
    {
        var mailCopyIdList = mailCopyIds.ToList();
        if (mailCopyIdList.Count == 0)
            return [];

        var metadatas = new List<MailFolderPairMetadata>();

        foreach (var idChunk in SqliteVariableLimit.Batch(mailCopyIdList))
        {
            var placeholders = string.Join(",", idChunk.Select(_ => "?"));
            var query = $"SELECT DISTINCT MailCopy.Id as MailCopyId, MailItemFolder.Id as FolderId, MailItemFolder.RemoteFolderId as RemoteFolderId FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailCopy.Id IN ({placeholders})";

            metadatas.AddRange(await Connection.QueryAsync<MailFolderPairMetadata>(query, idChunk.Cast<object>().ToArray()));
        }

        return metadatas;
    }

    public Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(string mailCopyId)
        => GetMailFolderPairMetadatasAsync(new List<string>() { mailCopyId });

    public async Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(MailSynchronizationOptions options)
    {
        var folders = new List<MailItemFolder>();

        if (options.Type == MailSynchronizationType.IMAPIdle)
        {
            // Type Inbox will include Sent, Drafts and Deleted folders as well.
            // For IMAP idle sync, we must include only Inbox folder.

            var inboxFolder = await GetSpecialFolderByAccountIdAsync(options.AccountId, SpecialFolderType.Inbox);

            if (inboxFolder != null)
            {
                folders.Add(inboxFolder);
            }
        }
        else if (options.Type == MailSynchronizationType.FullFolders)
        {
            // Only get sync enabled folders.

            var synchronizationFolders = await Connection.Table<MailItemFolder>()
                .Where(a => a.MailAccountId == options.AccountId && a.IsSynchronizationEnabled)
                .OrderBy(a => a.SpecialFolderType)
                .ToListAsync();

            folders.AddRange(synchronizationFolders);
        }
        else
        {
            // Inbox, Sent and Draft folders must always be synchronized regardless of whether they are enabled or not.
            // Custom folder sync will add additional folders to the list if not specified.

            var mustHaveFolders = await GetInboxSynchronizationFoldersAsync(options.AccountId);

            if (options.Type == MailSynchronizationType.InboxOnly)
            {
                return mustHaveFolders;
            }
            else if (options.Type == MailSynchronizationType.CustomFolders)
            {
                // Only get the specified folders.

                var synchronizationFolders = await Connection.Table<MailItemFolder>()
                    .Where(a =>
                    a.MailAccountId == options.AccountId &&
                    options.SynchronizationFolderIds.Contains(a.Id))
                    .ToListAsync();

                if (options.ExcludeMustHaveFolders)
                {
                    return synchronizationFolders;
                }

                // Order is important for moving.
                // By implementation, removing mail folders must be synchronized first. Requests are made in that order for custom sync.
                // eg. Moving item from Folder A to Folder B. If we start syncing Folder B first, we might miss adding assignment for Folder A.

                var orderedCustomFolders = synchronizationFolders.OrderBy(a => options.SynchronizationFolderIds.IndexOf(a.Id));

                foreach (var item in orderedCustomFolders)
                {
                    if (!mustHaveFolders.Any(a => a.Id == item.Id))
                    {
                        mustHaveFolders.Add(item);
                    }
                }
            }

            return mustHaveFolders;
        }

        return folders;
    }

    private async Task<List<MailItemFolder>> GetInboxSynchronizationFoldersAsync(Guid accountId)
    {
        var folders = new List<MailItemFolder>();

        var inboxFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Inbox);
        var sentFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Sent);
        var draftFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Draft);
        var deletedFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Deleted);

        if (deletedFolder != null)
        {
            folders.Add(deletedFolder);
        }

        if (inboxFolder != null)
        {
            folders.Add(inboxFolder);
        }

        // For properly creating threads we need Sent and Draft to be synchronized as well.

        if (sentFolder != null)
        {
            folders.Add(sentFolder);
        }

        if (draftFolder != null)
        {
            folders.Add(draftFolder);
        }

        return folders;
    }

    public Task<MailItemFolder> GetFolderAsync(Guid accountId, string remoteFolderId)
        => Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.RemoteFolderId == remoteFolderId);

    public async Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
    {
        var folder = await GetFolderAsync(accountId, remoteFolderId);

        if (folder == null)
        {
            _logger.Warning("Folder with id {RemoteFolderId} does not exist. Delete folder canceled.", remoteFolderId);

            return;
        }

        await DeleteFolderAsync(folder).ConfigureAwait(false);
    }

    public async Task ChangeFolderShowUnreadCountStateAsync(Guid folderId, bool showUnreadCount)
    {
        var localFolder = await GetFolderAsync(folderId);

        if (localFolder != null)
        {
            localFolder.ShowUnreadCount = showUnreadCount;

            await UpdateFolderAsync(localFolder).ConfigureAwait(false);
        }
    }

    public async Task ChangeFolderCountedInAccountTotalStateAsync(Guid folderId, bool isCounted)
    {
        var localFolder = await GetFolderAsync(folderId);

        if (localFolder != null)
        {
            localFolder.IsCountedInAccountTotal = isCounted;

            await UpdateFolderAsync(localFolder).ConfigureAwait(false);
        }
    }

    public async Task ChangeFolderJumpListStateAsync(Guid folderId, bool isEnabled)
    {
        var localFolder = await GetFolderAsync(folderId);

        if (localFolder != null)
        {
            localFolder.IsJumpListEnabled = isEnabled;

            await UpdateFolderAsync(localFolder).ConfigureAwait(false);
        }
    }

    public async Task<bool> IsInboxAvailableForAccountAsync(Guid accountId)
        => await Connection.Table<MailItemFolder>()
        .Where(a => a.SpecialFolderType == SpecialFolderType.Inbox && a.MailAccountId == accountId)
        .CountAsync() == 1;

    public Task UpdateFolderLastSyncDateAsync(Guid folderId)
        => Connection.ExecuteAsync("UPDATE MailItemFolder SET LastSynchronizedDate = ? WHERE Id = ?", DateTime.UtcNow, folderId);

    public Task<List<UnreadItemCountResult>> GetUnreadItemCountResultsAsync(IEnumerable<Guid> accountIds)
    {
        var accountIdList = accountIds.ToList();
        var placeholders = string.Join(",", accountIdList.Select(_ => "?"));
        var query = $"SELECT MailItemFolder.Id as FolderId, MailItemFolder.SpecialFolderType as SpecialFolderType, count(DISTINCT MailCopy.Id) as UnreadItemCount, MailItemFolder.MailAccountId as AccountId FROM MailCopy INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id WHERE MailItemFolder.MailAccountId IN ({placeholders}) AND MailCopy.IsRead = ? AND MailItemFolder.ShowUnreadCount = ? GROUP BY MailItemFolder.Id";
        var parameters = accountIdList.Cast<object>().Concat(new object[] { 0, 1 }).ToArray();
        
        return Connection.QueryAsync<UnreadItemCountResult>(query, parameters);
    }
}
