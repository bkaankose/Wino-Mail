using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

namespace Wino.Services;

public class MailCategoryService : BaseDatabaseService, IMailCategoryService
{
    public MailCategoryService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<List<MailCategory>> GetCategoriesAsync(Guid accountId)
        => Connection.QueryAsync<MailCategory>(
            $"SELECT * FROM {nameof(MailCategory)} WHERE {nameof(MailCategory.MailAccountId)} = ? ORDER BY {nameof(MailCategory.IsFavorite)} DESC, {nameof(MailCategory.Name)} COLLATE NOCASE",
            accountId);

    public Task<List<MailCategory>> GetFavoriteCategoriesAsync(Guid accountId)
        => Connection.QueryAsync<MailCategory>(
            $"SELECT * FROM {nameof(MailCategory)} WHERE {nameof(MailCategory.MailAccountId)} = ? AND {nameof(MailCategory.IsFavorite)} = 1 ORDER BY {nameof(MailCategory.Name)} COLLATE NOCASE",
            accountId);

    public Task<MailCategory> GetCategoryAsync(Guid categoryId)
        => Connection.FindAsync<MailCategory>(categoryId);

    public async Task<bool> CategoryNameExistsAsync(Guid accountId, string name, Guid? excludedCategoryId = null)
    {
        var normalizedName = NormalizeCategoryName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        var sql = $"SELECT COUNT(*) FROM {nameof(MailCategory)} WHERE {nameof(MailCategory.MailAccountId)} = ? AND lower(trim({nameof(MailCategory.Name)})) = ?";
        var parameters = new List<object> { accountId, normalizedName.ToLowerInvariant() };

        if (excludedCategoryId.HasValue)
        {
            sql += $" AND {nameof(MailCategory.Id)} <> ?";
            parameters.Add(excludedCategoryId.Value);
        }

        return await Connection.ExecuteScalarAsync<int>(sql, parameters.ToArray()).ConfigureAwait(false) > 0;
    }

    public async Task<MailCategory> CreateCategoryAsync(MailCategory category)
    {
        category.Id = category.Id == Guid.Empty ? Guid.NewGuid() : category.Id;
        category.Name = NormalizeCategoryName(category.Name);

        await Connection.InsertAsync(category, typeof(MailCategory)).ConfigureAwait(false);
        NotifyCategoryStructureChanged(category.MailAccountId);

        return category;
    }

    public async Task UpdateCategoryAsync(MailCategory category)
    {
        category.Name = NormalizeCategoryName(category.Name);

        await Connection.UpdateAsync(category, typeof(MailCategory)).ConfigureAwait(false);
        NotifyCategoryStructureChanged(category.MailAccountId);
    }

    public async Task DeleteCategoryAsync(Guid categoryId)
    {
        var category = await GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category == null)
            return;

        await Connection.ExecuteAsync($"DELETE FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCategoryId)} = ?", categoryId).ConfigureAwait(false);
        await Connection.DeleteAsync<MailCategory>(categoryId).ConfigureAwait(false);

        NotifyCategoryStructureChanged(category.MailAccountId);
    }

    public async Task DeleteCategoriesAsync(Guid accountId)
    {
        var categories = await GetCategoriesAsync(accountId).ConfigureAwait(false);

        if (categories.Count == 0)
            return;

        var categoryIds = categories.Select(a => a.Id).ToList();
        var placeholders = string.Join(",", categoryIds.Select(_ => "?"));
        var deleteAssignmentsSql = $"DELETE FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCategoryId)} IN ({placeholders})";

        await Connection.ExecuteAsync(deleteAssignmentsSql, categoryIds.Cast<object>().ToArray()).ConfigureAwait(false);
        await Connection.Table<MailCategory>().DeleteAsync(a => a.MailAccountId == accountId).ConfigureAwait(false);

        NotifyCategoryStructureChanged(accountId);
    }

    public async Task ToggleFavoriteAsync(Guid categoryId, bool isFavorite)
    {
        var category = await GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category == null || category.IsFavorite == isFavorite)
            return;

        category.IsFavorite = isFavorite;
        await Connection.UpdateAsync(category, typeof(MailCategory)).ConfigureAwait(false);

        NotifyCategoryStructureChanged(category.MailAccountId);
    }

    public async Task UpdateRemoteIdAsync(Guid categoryId, string remoteId)
    {
        var category = await GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category == null)
            return;

        category.RemoteId = remoteId;
        await Connection.UpdateAsync(category, typeof(MailCategory)).ConfigureAwait(false);
    }

    public async Task ReplaceCategoriesAsync(Guid accountId, IEnumerable<MailCategory> categories)
    {
        var existingCategories = await GetCategoriesAsync(accountId).ConfigureAwait(false);
        var existingByRemoteId = existingCategories
            .Where(a => !string.IsNullOrWhiteSpace(a.RemoteId))
            .ToDictionary(a => a.RemoteId, StringComparer.OrdinalIgnoreCase);
        var existingByName = existingCategories
            .GroupBy(a => NormalizeCategoryName(a.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Key, a => a.First(), StringComparer.OrdinalIgnoreCase);

        var incomingCategories = categories?.ToList() ?? [];
        var preservedIds = new HashSet<Guid>();

        foreach (var incoming in incomingCategories)
        {
            incoming.MailAccountId = accountId;
            incoming.Id = incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id;
            incoming.Name = NormalizeCategoryName(incoming.Name);

            MailCategory existing = null;

            if (!string.IsNullOrWhiteSpace(incoming.RemoteId) && existingByRemoteId.TryGetValue(incoming.RemoteId, out var byRemote))
            {
                existing = byRemote;
            }
            else if (existingByName.TryGetValue(incoming.Name, out var byName))
            {
                existing = byName;
            }

            if (existing == null)
            {
                await Connection.InsertAsync(incoming, typeof(MailCategory)).ConfigureAwait(false);
                preservedIds.Add(incoming.Id);
            }
            else
            {
                incoming.Id = existing.Id;
                incoming.IsFavorite = existing.IsFavorite;
                await Connection.UpdateAsync(incoming, typeof(MailCategory)).ConfigureAwait(false);
                preservedIds.Add(existing.Id);
            }
        }

        var categoryIdsToDelete = existingCategories
            .Where(a => !preservedIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToList();

        if (categoryIdsToDelete.Count > 0)
        {
            var placeholders = string.Join(",", categoryIdsToDelete.Select(_ => "?"));
            await Connection.ExecuteAsync(
                $"DELETE FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCategoryId)} IN ({placeholders})",
                categoryIdsToDelete.Cast<object>().ToArray()).ConfigureAwait(false);

            foreach (var categoryId in categoryIdsToDelete)
            {
                await Connection.DeleteAsync<MailCategory>(categoryId).ConfigureAwait(false);
            }
        }

        NotifyCategoryStructureChanged(accountId);
    }

    public async Task ReplaceMailAssignmentsAsync(Guid accountId, Guid mailCopyUniqueId, IEnumerable<string> categoryNames)
    {
        var normalizedNames = categoryNames?
            .Select(NormalizeCategoryName)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var availableCategories = await GetCategoriesAsync(accountId).ConfigureAwait(false);
        var categoryIds = availableCategories
            .Where(a => normalizedNames.Contains(NormalizeCategoryName(a.Name), StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Id)
            .ToHashSet();

        var existingAssignments = await Connection.QueryAsync<MailCategoryAssignment>(
            $"SELECT * FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCopyUniqueId)} = ?",
            mailCopyUniqueId).ConfigureAwait(false);

        var assignmentsToDelete = existingAssignments.Where(a => !categoryIds.Contains(a.MailCategoryId)).ToList();
        var existingIds = existingAssignments.Select(a => a.MailCategoryId).ToHashSet();
        var assignmentsToAdd = categoryIds.Where(a => !existingIds.Contains(a)).ToList();

        foreach (var assignment in assignmentsToDelete)
        {
            await Connection.DeleteAsync<MailCategoryAssignment>(assignment.Id).ConfigureAwait(false);
        }

        foreach (var categoryId in assignmentsToAdd)
        {
            await Connection.InsertAsync(new MailCategoryAssignment
            {
                Id = Guid.NewGuid(),
                MailCategoryId = categoryId,
                MailCopyUniqueId = mailCopyUniqueId
            }, typeof(MailCategoryAssignment)).ConfigureAwait(false);
        }

        WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(accountId));
    }

    public async Task AssignCategoryAsync(Guid categoryId, IEnumerable<Guid> mailCopyUniqueIds)
    {
        var uniqueIds = mailCopyUniqueIds?.Distinct().ToList() ?? [];
        if (uniqueIds.Count == 0)
            return;

        var category = await GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category == null)
            return;

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        var query = $"SELECT * FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCategoryId)} = ? AND {nameof(MailCategoryAssignment.MailCopyUniqueId)} IN ({placeholders})";
        var existingAssignments = await Connection.QueryAsync<MailCategoryAssignment>(
            query,
            [categoryId, .. uniqueIds.Cast<object>()]).ConfigureAwait(false);
        var existingUniqueIds = existingAssignments.Select(a => a.MailCopyUniqueId).ToHashSet();

        foreach (var uniqueId in uniqueIds.Where(a => !existingUniqueIds.Contains(a)))
        {
            await Connection.InsertAsync(new MailCategoryAssignment
            {
                Id = Guid.NewGuid(),
                MailCategoryId = categoryId,
                MailCopyUniqueId = uniqueId
            }, typeof(MailCategoryAssignment)).ConfigureAwait(false);
        }

        WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(category.MailAccountId));
    }

    public async Task UnassignCategoryAsync(Guid categoryId, IEnumerable<Guid> mailCopyUniqueIds)
    {
        var uniqueIds = mailCopyUniqueIds?.Distinct().ToList() ?? [];
        if (uniqueIds.Count == 0)
            return;

        var category = await GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category == null)
            return;

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        await Connection.ExecuteAsync(
            $"DELETE FROM {nameof(MailCategoryAssignment)} WHERE {nameof(MailCategoryAssignment.MailCategoryId)} = ? AND {nameof(MailCategoryAssignment.MailCopyUniqueId)} IN ({placeholders})",
            [categoryId, .. uniqueIds.Cast<object>()]).ConfigureAwait(false);

        WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(category.MailAccountId));
    }

    public async Task<List<MailCategory>> GetCategoriesForMailAsync(Guid accountId, IEnumerable<Guid> mailCopyUniqueIds)
    {
        var uniqueIds = mailCopyUniqueIds?.Distinct().ToList() ?? [];
        if (uniqueIds.Count == 0)
            return [];

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        var sql = $"SELECT DISTINCT MailCategory.* FROM {nameof(MailCategory)} " +
                  $"INNER JOIN {nameof(MailCategoryAssignment)} ON {nameof(MailCategory)}.{nameof(MailCategory.Id)} = {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCategoryId)} " +
                  $"WHERE {nameof(MailCategory)}.{nameof(MailCategory.MailAccountId)} = ? AND {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCopyUniqueId)} IN ({placeholders}) " +
                  $"ORDER BY {nameof(MailCategory.Name)} COLLATE NOCASE";

        return await Connection.QueryAsync<MailCategory>(
            sql,
            [accountId, .. uniqueIds.Cast<object>()]).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MailCategory>>> GetCategoriesByMailAsync(Guid accountId, IEnumerable<Guid> mailCopyUniqueIds)
    {
        var uniqueIds = mailCopyUniqueIds?.Distinct().ToList() ?? [];
        if (uniqueIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<MailCategory>>();

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        var sql =
            $"SELECT {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCopyUniqueId)} as {nameof(MailCategoryRow.MailCopyUniqueId)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.Id)} as {nameof(MailCategoryRow.Id)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.MailAccountId)} as {nameof(MailCategoryRow.MailAccountId)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.RemoteId)} as {nameof(MailCategoryRow.RemoteId)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.Name)} as {nameof(MailCategoryRow.Name)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.IsFavorite)} as {nameof(MailCategoryRow.IsFavorite)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.BackgroundColorHex)} as {nameof(MailCategoryRow.BackgroundColorHex)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.TextColorHex)} as {nameof(MailCategoryRow.TextColorHex)}, " +
            $"{nameof(MailCategory)}.{nameof(MailCategory.Source)} as {nameof(MailCategoryRow.Source)} " +
            $"FROM {nameof(MailCategory)} " +
            $"INNER JOIN {nameof(MailCategoryAssignment)} ON {nameof(MailCategory)}.{nameof(MailCategory.Id)} = {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCategoryId)} " +
            $"WHERE {nameof(MailCategory)}.{nameof(MailCategory.MailAccountId)} = ? AND {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCopyUniqueId)} IN ({placeholders}) " +
            $"ORDER BY {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCopyUniqueId)}, {nameof(MailCategory)}.{nameof(MailCategory.Name)} COLLATE NOCASE";

        var rows = await Connection.QueryAsync<MailCategoryRow>(
            sql,
            [accountId, .. uniqueIds.Cast<object>()]).ConfigureAwait(false);

        return rows
            .GroupBy(a => a.MailCopyUniqueId)
            .ToDictionary(
                a => a.Key,
                a => (IReadOnlyList<MailCategory>)a.Select(static row => row.ToMailCategory()).ToList());
    }

    public async Task<List<Guid>> GetAssignedCategoryIdsForAllAsync(IEnumerable<Guid> mailCopyUniqueIds)
    {
        var uniqueIds = mailCopyUniqueIds?.Distinct().ToList() ?? [];
        if (uniqueIds.Count == 0)
            return [];

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        var sql = $"SELECT {nameof(MailCategoryAssignment.MailCategoryId)} " +
                  $"FROM {nameof(MailCategoryAssignment)} " +
                  $"WHERE {nameof(MailCategoryAssignment.MailCopyUniqueId)} IN ({placeholders}) " +
                  $"GROUP BY {nameof(MailCategoryAssignment.MailCategoryId)} " +
                  $"HAVING COUNT(DISTINCT {nameof(MailCategoryAssignment.MailCopyUniqueId)}) = ?";

        return await Connection.QueryScalarsAsync<Guid>(
            sql,
            [.. uniqueIds.Cast<object>(), uniqueIds.Count]).ConfigureAwait(false);
    }

    public async Task<List<string>> GetCategoryNamesForMailAsync(Guid mailCopyUniqueId)
    {
        var sql = $"SELECT {nameof(MailCategory.Name)} " +
                  $"FROM {nameof(MailCategory)} " +
                  $"INNER JOIN {nameof(MailCategoryAssignment)} ON {nameof(MailCategory)}.{nameof(MailCategory.Id)} = {nameof(MailCategoryAssignment.MailCategoryId)} " +
                  $"WHERE {nameof(MailCategoryAssignment.MailCopyUniqueId)} = ? " +
                  $"ORDER BY {nameof(MailCategory.Name)} COLLATE NOCASE";

        return await Connection.QueryScalarsAsync<string>(sql, mailCopyUniqueId).ConfigureAwait(false);
    }

    public async Task<List<MailCopy>> GetMailCopiesForCategoryAsync(Guid categoryId)
    {
        var sql = $"SELECT {nameof(MailCopy)}.* " +
                  $"FROM {nameof(MailCopy)} " +
                  $"INNER JOIN {nameof(MailCategoryAssignment)} ON {nameof(MailCopy)}.{nameof(MailCopy.UniqueId)} = {nameof(MailCategoryAssignment.MailCopyUniqueId)} " +
                  $"WHERE {nameof(MailCategoryAssignment.MailCategoryId)} = ?";

        return await Connection.QueryAsync<MailCopy>(sql, categoryId).ConfigureAwait(false);
    }

    public Task<List<UnreadCategoryCountResult>> GetUnreadCategoryCountResultsAsync(IEnumerable<Guid> accountIds)
    {
        var accountIdList = accountIds?.Distinct().ToList() ?? [];
        if (accountIdList.Count == 0)
            return Task.FromResult(new List<UnreadCategoryCountResult>());

        var placeholders = string.Join(",", accountIdList.Select(_ => "?"));
        var sql =
            $"SELECT MailCategory.{nameof(MailCategory.Id)} as {nameof(UnreadCategoryCountResult.CategoryId)}, " +
            $"MailCategory.{nameof(MailCategory.MailAccountId)} as {nameof(UnreadCategoryCountResult.AccountId)}, " +
            $"COUNT(DISTINCT MailCopy.{nameof(MailCopy.UniqueId)}) as {nameof(UnreadCategoryCountResult.UnreadItemCount)} " +
            $"FROM {nameof(MailCategory)} " +
            $"INNER JOIN {nameof(MailCategoryAssignment)} ON {nameof(MailCategory)}.{nameof(MailCategory.Id)} = {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCategoryId)} " +
            $"INNER JOIN {nameof(MailCopy)} ON {nameof(MailCategoryAssignment)}.{nameof(MailCategoryAssignment.MailCopyUniqueId)} = {nameof(MailCopy)}.{nameof(MailCopy.UniqueId)} " +
            $"WHERE MailCategory.{nameof(MailCategory.MailAccountId)} IN ({placeholders}) AND MailCopy.{nameof(MailCopy.IsRead)} = 0 " +
            $"GROUP BY MailCategory.{nameof(MailCategory.Id)}";

        return Connection.QueryAsync<UnreadCategoryCountResult>(sql, accountIdList.Cast<object>().ToArray());
    }

    private void NotifyCategoryStructureChanged(Guid accountId)
    {
        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
        WeakReferenceMessenger.Default.Send(new RefreshUnreadCountsMessage(accountId));
    }

    private static string NormalizeCategoryName(string name)
        => name?.Trim() ?? string.Empty;

    private sealed class MailCategoryRow : MailCategory
    {
        public Guid MailCopyUniqueId { get; set; }

        public MailCategory ToMailCategory() => new()
        {
            Id = Id,
            MailAccountId = MailAccountId,
            RemoteId = RemoteId,
            Name = Name,
            IsFavorite = IsFavorite,
            BackgroundColorHex = BackgroundColorHex,
            TextColorHex = TextColorHex,
            Source = Source
        };
    }
}
