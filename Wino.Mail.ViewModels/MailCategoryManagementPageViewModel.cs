using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Requests.Category;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels;

public partial class MailCategoryManagementPageViewModel : MailBaseViewModel
{
    private readonly IMailCategoryService _mailCategoryService;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    public partial MailAccount Account { get; set; }

    public ObservableCollection<MailCategory> Categories { get; } = [];

    public bool CanRefresh => Account?.ProviderType == MailProviderType.Outlook;
    public bool HasCategories => Categories.Count > 0;

    public MailCategoryManagementPageViewModel(
        IMailCategoryService mailCategoryService,
        IAccountService accountService,
        IMailDialogService dialogService,
        IWinoRequestDelegator winoRequestDelegator)
    {
        _mailCategoryService = mailCategoryService;
        _accountService = accountService;
        _dialogService = dialogService;
        _winoRequestDelegator = winoRequestDelegator;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        Account = await _accountService.GetAccountAsync(accountId);

        if (Account != null)
        {
            await LoadCategoriesAsync();
        }
    }

    [RelayCommand]
    private Task AddCategoryAsync()
        => CreateOrUpdateCategoryAsync();

    [RelayCommand]
    private async Task RefreshCategoriesAsync()
    {
        if (!CanRefresh)
            return;

        var shouldContinue = await _dialogService.ShowConfirmationDialogAsync(
            Translator.MailCategoryManagementPage_RefreshConfirmationMessage,
            Translator.Buttons_Refresh,
            Translator.Buttons_Refresh).ConfigureAwait(false);

        if (!shouldContinue)
            return;

        await _mailCategoryService.DeleteCategoriesAsync(Account.Id).ConfigureAwait(false);
        await SynchronizationManager.Instance.SynchronizeCategoriesAsync(Account.Id).ConfigureAwait(false);

        await LoadCategoriesAsync().ConfigureAwait(false);
    }

    public Task EditCategoryAsync(MailCategory category)
        => CreateOrUpdateCategoryAsync(category);

    public async Task DeleteCategoryAsync(MailCategory category)
    {
        if (category == null)
            return;

        var shouldDelete = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.MailCategoryManagementPage_DeleteConfirmationMessage, category.Name),
            Translator.MailCategoryManagementPage_DeleteConfirmationTitle,
            Translator.Buttons_Delete).ConfigureAwait(false);

        if (!shouldDelete)
            return;

        var deleteRequest = await BuildDeleteCategoryRequestAsync(category).ConfigureAwait(false);
        await _mailCategoryService.DeleteCategoryAsync(category.Id).ConfigureAwait(false);
        await QueueOutlookCategoryRequestsAsync(deleteRequest).ConfigureAwait(false);
        await LoadCategoriesAsync().ConfigureAwait(false);
    }

    public async Task SetFavoriteAsync(MailCategory category, bool isFavorite)
    {
        if (category == null)
            return;

        await _mailCategoryService.ToggleFavoriteAsync(category.Id, isFavorite).ConfigureAwait(false);
        await LoadCategoriesAsync().ConfigureAwait(false);
    }

    private async Task CreateOrUpdateCategoryAsync(MailCategory existingCategory = null)
    {
        var dialogResult = await _dialogService.ShowEditMailCategoryDialogAsync(existingCategory).ConfigureAwait(false);
        if (dialogResult == null)
            return;

        if (string.IsNullOrWhiteSpace(dialogResult.Name))
        {
            await _dialogService.ShowMessageAsync(
                Translator.MailCategoryDialog_InvalidNameMessage,
                Translator.MailCategoryDialog_InvalidNameTitle,
                WinoCustomMessageDialogIcon.Warning).ConfigureAwait(false);
            return;
        }

        var normalizedName = dialogResult.Name.Trim();
        var categoryIdToExclude = existingCategory?.Id;
        var alreadyExists = await _mailCategoryService.CategoryNameExistsAsync(Account.Id, normalizedName, categoryIdToExclude).ConfigureAwait(false);

        if (alreadyExists)
        {
            await _dialogService.ShowMessageAsync(
                Translator.MailCategoryDialog_DuplicateMessage,
                Translator.MailCategoryDialog_DuplicateTitle,
                WinoCustomMessageDialogIcon.Warning).ConfigureAwait(false);
            return;
        }

        if (existingCategory == null)
        {
            var newCategory = new MailCategory
            {
                Id = Guid.NewGuid(),
                MailAccountId = Account.Id,
                Name = normalizedName,
                BackgroundColorHex = dialogResult.BackgroundColorHex,
                TextColorHex = dialogResult.TextColorHex,
                Source = Account.ProviderType == MailProviderType.Outlook ? MailCategorySource.Outlook : MailCategorySource.Local
            };

            await _mailCategoryService.CreateCategoryAsync(newCategory).ConfigureAwait(false);

            if (Account.ProviderType == MailProviderType.Outlook)
            {
                await _winoRequestDelegator.ExecuteAsync(Account.Id, [new MailCategoryCreateRequest(newCategory)]).ConfigureAwait(false);
            }
        }
        else
        {
            var previousName = existingCategory.Name;
            var previousRemoteId = existingCategory.RemoteId;

            existingCategory.Name = normalizedName;
            existingCategory.BackgroundColorHex = dialogResult.BackgroundColorHex;
            existingCategory.TextColorHex = dialogResult.TextColorHex;

            await _mailCategoryService.UpdateCategoryAsync(existingCategory).ConfigureAwait(false);

            if (Account.ProviderType == MailProviderType.Outlook)
            {
                if (string.IsNullOrWhiteSpace(previousRemoteId))
                {
                    await _winoRequestDelegator.ExecuteAsync(Account.Id, [new MailCategoryCreateRequest(existingCategory)]).ConfigureAwait(false);
                }
                else
                {
                    var affectedMessages = await BuildAffectedMessageTargetsAsync(existingCategory.Id).ConfigureAwait(false);
                    var updateRequest = new MailCategoryUpdateRequest(existingCategory, previousName, previousRemoteId, affectedMessages);
                    await _winoRequestDelegator.ExecuteAsync(Account.Id, [updateRequest]).ConfigureAwait(false);
                }
            }
        }

        await LoadCategoriesAsync().ConfigureAwait(false);
    }

    private async Task<MailCategoryDeleteRequest> BuildDeleteCategoryRequestAsync(MailCategory category)
    {
        if (category == null || Account?.ProviderType != MailProviderType.Outlook)
            return null;

        var mailCopies = await _mailCategoryService.GetMailCopiesForCategoryAsync(category.Id).ConfigureAwait(false);
        var affectedMessages = new List<MailCategoryMessageUpdateTarget>();

        foreach (var mailCopy in mailCopies.Where(a => !string.IsNullOrWhiteSpace(a.Id)))
        {
            var remainingNames = await _mailCategoryService.GetCategoryNamesForMailAsync(mailCopy.UniqueId).ConfigureAwait(false);
            var categoryNames = remainingNames
                .Where(a => !string.Equals(a, category.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            affectedMessages.Add(new MailCategoryMessageUpdateTarget(mailCopy.Id, categoryNames));
        }

        return new MailCategoryDeleteRequest(category, category.RemoteId, affectedMessages);
    }

    private async Task<IReadOnlyList<MailCategoryMessageUpdateTarget>> BuildAffectedMessageTargetsAsync(Guid categoryId)
    {
        var mailCopies = await _mailCategoryService.GetMailCopiesForCategoryAsync(categoryId).ConfigureAwait(false);
        var affectedMessages = new List<MailCategoryMessageUpdateTarget>();

        foreach (var mailCopy in mailCopies.Where(a => !string.IsNullOrWhiteSpace(a.Id)))
        {
            var categoryNames = await _mailCategoryService.GetCategoryNamesForMailAsync(mailCopy.UniqueId).ConfigureAwait(false);
            affectedMessages.Add(new MailCategoryMessageUpdateTarget(mailCopy.Id, categoryNames));
        }

        return affectedMessages;
    }

    private Task QueueOutlookCategoryRequestsAsync(params IRequestBase[] requests)
        => Account?.ProviderType == MailProviderType.Outlook && requests.Any(a => a != null)
            ? _winoRequestDelegator.ExecuteAsync(Account.Id, requests.Where(a => a != null))
            : Task.CompletedTask;

    private async Task LoadCategoriesAsync()
    {
        var categories = await _mailCategoryService.GetCategoriesAsync(Account.Id).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            Categories.Clear();

            foreach (var category in categories)
            {
                Categories.Add(category);
            }

            OnPropertyChanged(nameof(HasCategories));
        });
    }
}
