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
using Wino.Core.Services;

namespace Wino.Mail.ViewModels;

public partial class MailCategoryManagementPageViewModel : MailBaseViewModel
{
    private readonly IMailCategoryService _mailCategoryService;
    private readonly IAccountService _accountService;
    private readonly IMailDialogService _dialogService;
    private readonly CategoryEditor _categoryEditor;

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
        IWinoRequestDelegator winoRequestDelegator,
        IContactQueryService contactService)
    {
        _mailCategoryService = mailCategoryService;
        _accountService = accountService;
        _dialogService = dialogService;
        _categoryEditor = new CategoryEditor(mailCategoryService, contactService, dialogService, winoRequestDelegator);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        => await InitializeAsync(mode, parameters);

    public async Task InitializeAsync(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        await ExecuteUIThread(() => Account = account).ConfigureAwait(false);

        if (account != null)
        {
            await LoadCategoriesAsync().ConfigureAwait(false);
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

        var shouldContinue = await ExecuteUIThreadAsync(() =>
            _dialogService.ShowConfirmationDialogAsync(
                Translator.MailCategoryManagementPage_RefreshConfirmationMessage,
                Translator.Buttons_Refresh,
                Translator.Buttons_Refresh)).ConfigureAwait(false);

        if (!shouldContinue)
            return;

        // Only the categories Outlook owns are rebuilt. Those kept on the device stay.
        await _mailCategoryService.DeleteCategoriesAsync(Account.Id, MailCategorySource.Outlook).ConfigureAwait(false);
        await SynchronizationManager.Instance.SynchronizeCategoriesAsync(Account.Id).ConfigureAwait(false);

        await LoadCategoriesAsync().ConfigureAwait(false);
    }

    public Task EditCategoryAsync(MailCategory category)
        => CreateOrUpdateCategoryAsync(category);

    public async Task DeleteCategoryAsync(MailCategory category)
    {
        if (await ExecuteUIThreadAsync(() => _categoryEditor.DeleteAsync(Account, category)).ConfigureAwait(false))
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
        if (await ExecuteUIThreadAsync(() => _categoryEditor.CreateOrUpdateAsync(Account, existingCategory)).ConfigureAwait(false))
            await LoadCategoriesAsync().ConfigureAwait(false);
    }

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
