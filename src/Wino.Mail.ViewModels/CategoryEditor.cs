using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Requests.Category;
using Wino.Core.Requests.Contact;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Creates, edits and deletes the categories of an account. A category is defined once
/// per account and is applied to mails and contacts alike, so the category page and the
/// contacts pane both edit through here.
/// The change is stored first and then handed to the account's synchronizer, which sends
/// it wherever the category also lives: the Outlook category list, the mails and the
/// contacts that carry its name.
/// </summary>
public sealed class CategoryEditor
{
    private readonly IMailCategoryService _categoryService;
    private readonly IContactQueryService _contactService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoRequestDelegator _requestDelegator;

    public CategoryEditor(
        IMailCategoryService categoryService,
        IContactQueryService contactService,
        IMailDialogService dialogService,
        IWinoRequestDelegator requestDelegator)
    {
        _categoryService = categoryService;
        _contactService = contactService;
        _dialogService = dialogService;
        _requestDelegator = requestDelegator;
    }

    /// <summary>
    /// Asks for a name and a color, then creates the category or applies the edit.
    /// Shows dialogs, so it must be called on the UI thread. Returns false when nothing changed.
    /// </summary>
    public async Task<bool> CreateOrUpdateAsync(MailAccount account, MailCategory existingCategory = null)
    {
        var dialogResult = await _dialogService.ShowEditMailCategoryDialogAsync(existingCategory);
        if (dialogResult == null)
            return false;

        if (string.IsNullOrWhiteSpace(dialogResult.Name))
        {
            await _dialogService.ShowMessageAsync(
                Translator.MailCategoryDialog_InvalidNameMessage,
                Translator.MailCategoryDialog_InvalidNameTitle,
                WinoCustomMessageDialogIcon.Warning);
            return false;
        }

        var name = dialogResult.Name.Trim();

        if (await _categoryService.CategoryNameExistsAsync(account.Id, name, existingCategory?.Id))
        {
            await _dialogService.ShowMessageAsync(
                Translator.MailCategoryDialog_DuplicateMessage,
                Translator.MailCategoryDialog_DuplicateTitle,
                WinoCustomMessageDialogIcon.Warning);
            return false;
        }

        if (existingCategory == null)
        {
            var category = new MailCategory
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                Name = name,
                BackgroundColorHex = dialogResult.BackgroundColorHex,
                TextColorHex = dialogResult.TextColorHex,
                Source = account.ProviderType == MailProviderType.Outlook ? MailCategorySource.Outlook : MailCategorySource.Local
            };

            await _categoryService.CreateCategoryAsync(category);
            await _requestDelegator.ExecuteAsync(account.Id, new IRequestBase[] { new MailCategoryCreateRequest(category) });
            return true;
        }

        var previousName = existingCategory.Name;
        var previousRemoteId = existingCategory.RemoteId;
        var isRenamed = !string.Equals(previousName, name, StringComparison.Ordinal);

        existingCategory.Name = name;
        existingCategory.BackgroundColorHex = dialogResult.BackgroundColorHex;
        existingCategory.TextColorHex = dialogResult.TextColorHex;

        await _categoryService.UpdateCategoryAsync(existingCategory);

        var affectedMessages = await GetAffectedMessagesAsync(account, existingCategory, removedName: null);
        await _requestDelegator.ExecuteAsync(
            account.Id,
            new IRequestBase[] { new MailCategoryUpdateRequest(existingCategory, previousName, previousRemoteId, affectedMessages) });

        // A contact carries its categories by name, so a rename is written to each of them.
        if (isRenamed)
        {
            var contacts = await _contactService.GetContactsByCategoryAsync(existingCategory.Id);
            await QueueContactRequestsAsync(account, contacts.Select(contact => new ContactCategoryRequest(contact, contact.Categories)));
        }

        return true;
    }

    /// <summary>
    /// Deletes the category after a confirmation and takes it off every mail and contact.
    /// Shows a dialog, so it must be called on the UI thread. Returns false when nothing changed.
    /// </summary>
    public async Task<bool> DeleteAsync(MailAccount account, MailCategory category)
    {
        if (category == null)
            return false;

        var shouldDelete = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.MailCategoryManagementPage_DeleteConfirmationMessage, category.Name),
            Translator.MailCategoryManagementPage_DeleteConfirmationTitle,
            Translator.Buttons_Delete);

        if (!shouldDelete)
            return false;

        // Both are read before the category and its assignments are gone.
        var affectedMessages = await GetAffectedMessagesAsync(account, category, removedName: category.Name);
        var contacts = await _contactService.GetContactsByCategoryAsync(category.Id);

        await _categoryService.DeleteCategoryAsync(category.Id);
        await _requestDelegator.ExecuteAsync(
            account.Id,
            new IRequestBase[] { new MailCategoryDeleteRequest(category, category.RemoteId, affectedMessages) });

        await QueueContactRequestsAsync(account, contacts.Select(contact =>
            new ContactCategoryRequest(contact, contact.Categories.Where(item => item.Id != category.Id))));

        return true;
    }

    private Task QueueContactRequestsAsync(MailAccount account, IEnumerable<ContactCategoryRequest> requests)
    {
        var contactRequests = requests.Cast<IRequestBase>().ToList();
        return contactRequests.Count == 0
            ? Task.CompletedTask
            : _requestDelegator.ExecuteAsync(account.Id, contactRequests);
    }

    /// <summary>
    /// Outlook stores a mail's categories on the mail by name, so a renamed or removed
    /// category is rewritten on every mail that carries it. Other providers keep mail
    /// categories on the device, where the change is already stored.
    /// </summary>
    private async Task<IReadOnlyList<MailCategoryMessageUpdateTarget>> GetAffectedMessagesAsync(MailAccount account, MailCategory category, string removedName)
    {
        if (account.ProviderType != MailProviderType.Outlook || category.Source != MailCategorySource.Outlook)
            return [];

        var mailCopies = await _categoryService.GetMailCopiesForCategoryAsync(category.Id);
        var affectedMessages = new List<MailCategoryMessageUpdateTarget>();

        foreach (var mailCopy in mailCopies.Where(mail => !string.IsNullOrWhiteSpace(mail.Id)))
        {
            var categoryNames = (await _categoryService.GetCategoryNamesForMailAsync(mailCopy.UniqueId))
                .Where(name => removedName is null || !string.Equals(name, removedName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            affectedMessages.Add(new MailCategoryMessageUpdateTarget(mailCopy.Id, categoryNames));
        }

        return affectedMessages;
    }
}
