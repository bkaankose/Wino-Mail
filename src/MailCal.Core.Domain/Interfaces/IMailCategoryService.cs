using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Accounts;

namespace MailCal.Core.Domain.Interfaces;

public interface IMailCategoryService
{
    Task<List<MailCategory>> GetCategoriesAsync(Guid accountId);
    Task<List<MailCategory>> GetFavoriteCategoriesAsync(Guid accountId);
    Task<MailCategory> GetCategoryAsync(Guid categoryId);
    Task<bool> CategoryNameExistsAsync(Guid accountId, string name, Guid? excludedCategoryId = null);
    Task<MailCategory> CreateCategoryAsync(MailCategory category);
    Task UpdateCategoryAsync(MailCategory category);
    Task DeleteCategoryAsync(Guid categoryId);
    Task DeleteCategoriesAsync(Guid accountId, MailCategorySource? source = null);
    Task ToggleFavoriteAsync(Guid categoryId, bool isFavorite);
    Task UpdateRemoteIdAsync(Guid categoryId, string remoteId);
    Task ReplaceCategoriesAsync(Guid accountId, IEnumerable<MailCategory> categories);
    Task ReplaceMailAssignmentsAsync(Guid accountId, Guid mailCopyUniqueId, IEnumerable<string> categoryNames);
    Task AssignCategoryAsync(Guid categoryId, IEnumerable<Guid> mailCopyUniqueIds);
    Task UnassignCategoryAsync(Guid categoryId, IEnumerable<Guid> mailCopyUniqueIds);
    Task<List<MailCategory>> GetCategoriesForMailAsync(Guid accountId, IEnumerable<Guid> mailCopyUniqueIds);
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<MailCategory>>> GetCategoriesByMailAsync(Guid accountId, IEnumerable<Guid> mailCopyUniqueIds);
    Task<List<Guid>> GetAssignedCategoryIdsForAllAsync(IEnumerable<Guid> mailCopyUniqueIds);
    Task<List<string>> GetCategoryNamesForMailAsync(Guid mailCopyUniqueId);
    Task<List<MailCopy>> GetMailCopiesForCategoryAsync(Guid categoryId);
    Task<List<UnreadCategoryCountResult>> GetUnreadCategoryCountResultsAsync(IEnumerable<Guid> accountIds);
}
