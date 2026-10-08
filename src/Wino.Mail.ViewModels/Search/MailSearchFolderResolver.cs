using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Search;

/// <summary>
/// Turns a search scope into the folders a local search reads. The accounts are always the ones
/// behind the active folder, so a merged folder searches each of its accounts and an account
/// folder never reaches into another account.
/// </summary>
public static class MailSearchFolderResolver
{
    public static async Task<IReadOnlyList<IMailItemFolder>> ResolveAsync(
        MailSearchScope scope,
        IEnumerable<IMailItemFolder> activeFolders,
        Func<Guid, Task<IReadOnlyList<IMailItemFolder>>> loadAccountFolders)
    {
        ArgumentNullException.ThrowIfNull(loadAccountFolders);

        var current = Distinct(activeFolders ?? []);
        if (scope == MailSearchScope.CurrentFolder || current.Count == 0)
            return current;

        var result = new List<IMailItemFolder>();
        foreach (var accountFolders in current.GroupBy(folder => folder.MailAccountId))
        {
            var allFolders = Distinct(await loadAccountFolders(accountFolders.Key).ConfigureAwait(false) ?? []);

            if (scope == MailSearchScope.AllFolders)
                result.AddRange(allFolders);
            else
                result.AddRange(WithDescendants(accountFolders, allFolders));
        }

        return Distinct(result);
    }

    public static bool IsSearchable(IMailItemFolder folder)
        => folder is not null && !string.IsNullOrWhiteSpace(folder.RemoteFolderId);

    private static IEnumerable<IMailItemFolder> WithDescendants(
        IEnumerable<IMailItemFolder> roots,
        IReadOnlyList<IMailItemFolder> accountFolders)
    {
        var childrenByParent = accountFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.ParentRemoteFolderId))
            .ToLookup(folder => folder.ParentRemoteFolderId, StringComparer.Ordinal);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<IMailItemFolder>(roots);
        while (pending.Count > 0)
        {
            var folder = pending.Dequeue();
            if (!visited.Add(folder.RemoteFolderId)) continue;

            yield return folder;
            foreach (var child in childrenByParent[folder.RemoteFolderId])
                pending.Enqueue(child);
        }
    }

    private static List<IMailItemFolder> Distinct(IEnumerable<IMailItemFolder> folders)
        => folders
            .Where(IsSearchable)
            .GroupBy(folder => folder.Id)
            .Select(group => group.First())
            .ToList();
}
